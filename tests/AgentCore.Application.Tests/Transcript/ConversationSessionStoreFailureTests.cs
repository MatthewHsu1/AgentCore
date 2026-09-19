using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// What store 1 owes the chain: the words first, and a dropped write that costs the conversation nothing.
/// </summary>
/// <remarks>
/// The chain stores a hash of the spoken text and store 1 stores the text. That split only works in
/// one order — the words have to be there before the row that names their digest — and it only works
/// if a store that refuses a write cannot end the conversation it is recording.
/// </remarks>
public sealed class ConversationSessionStoreFailureTests
{
    private const string OneAgentYaml = """
        apiVersion: agentcore/v1
        agents:
          # These tests read the exact messages the model sees; the clock line would be one more.
          defaults: { clock: false }
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            agent: only
        """;

    /// <summary>
    /// The <c>reply.interrupted</c> row names a hash of the words the caller heard. Raised before
    /// store 1 holds them, it names a hash of words nothing holds.
    /// </summary>
    [Fact]
    public async Task Interrupt_MidReply_StoreOneHoldsTheWordsBeforeTheInterruptedFactIsRaised()
    {
        // Arrange
        RecordingConversationStore store = new();
        using ScriptedChatClient reply = new("Hello", " there", " caller") { GateAfterFirstFragment = true };
        List<string> whenTheFactWasRaised = [];
        WatchingObserver observer = new(
            ConversationEventKind.ReplyInterrupted,
            conversationEvent => whenTheFactWasRaised.AddRange(
                store.Live(conversationEvent.ConversationId).Select(row => row.Content.Text)));
        var session = CreateSession(OneAgentYaml, reply, store, observer);
        var (turn, spoke) = StartGatedTurn(session, "hi");
        await spoke;

        // Act
        var recorded = session.Interrupt("Hello", TimeSpan.FromMilliseconds(300));

        // Assert
        reply.OpenGate();
        await turn;
        Assert.True(recorded);
        Assert.Equal(["hi", "Hello"], whenTheFactWasRaised);
    }

    /// <summary>A store 1 write failure never ends a conversation.</summary>
    [Fact]
    public async Task Append_StoreThrows_ConversationContinues()
    {
        // Arrange
        using RequestRecordingChatClient reply = new("hi there", "it ships Friday");
        var session = CreateSession(OneAgentYaml, reply, new ThrowingConversationStore());
        _ = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        // Act
        var second = await session.RunTurnAsync("order 41?", TestContext.Current.CancellationToken);

        // Assert. The live history is the session's, so the turn after a dropped write still has the
        // whole conversation. Only the durable copy was lost.
        Assert.Equal("it ships Friday", second.ReplyText);
        Assert.Equal(
            ["user:hello", "assistant:hi there", "user:order 41?"],
            reply.Requests[1]);
    }

    /// <summary>
    /// A turn that cannot re-read store 1 as it opens says so, and runs on the words it holds.
    /// </summary>
    [Fact]
    public async Task Resync_StoreThrows_RaisesDiagnosticAndTheTurnRuns()
    {
        // Arrange
        using RequestRecordingChatClient reply = new("hi there", "it ships Friday");
        RecordingObserver observer = new();
        var session = CreateSession(OneAgentYaml, reply, new ThrowingConversationStore(), observer);
        _ = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        // Act
        var second = await session.RunTurnAsync("order 41?", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("it ships Friday", second.ReplyText);
        var failed = Assert.Single(
            observer.Events,
            conversationEvent => conversationEvent.Kind == ConversationEventKind.TranscriptResyncFailed);
        Assert.Null(failed.EventId);
        Assert.Equal(1, failed.TurnIndex);
    }

    /// <summary>
    /// A dropped write is a fact about the system and never about the conversation, so it is counted and
    /// logged and stored nowhere.
    /// </summary>
    [Fact]
    public async Task Append_StoreThrows_RaisesDiagnosticWithNoEventId()
    {
        // Arrange
        using RequestRecordingChatClient reply = new("hi there");
        RecordingObserver observer = new();
        var session = CreateSession(OneAgentYaml, reply, new ThrowingConversationStore(), observer);

        // Act
        _ = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        // Assert
        await session.FlushTranscriptAsync();
        var dropped = Assert.Single(
            observer.Events,
            conversationEvent => conversationEvent.Kind == ConversationEventKind.TranscriptWriteFailed);
        Assert.Null(dropped.EventId);
        Assert.Equal(0, dropped.TurnIndex);

        // The facts that did take an identity are the ones the chain would have written with no
        // failure at all: only the dropped write itself takes no row.
        var stored = observer.Events.Where(item => item.EventId is not null).ToArray();
        Assert.Equal(
            [ConversationEventKind.ConversationStarted, ConversationEventKind.TurnCompleted],
            stored.Select(item => item.Kind).ToArray());
        Assert.Equal(stored.Length, stored.Select(item => item.EventId).Distinct().Count());
    }

    private static ConversationSession CreateSession(
        string yaml, IChatClient reply, IConversationStore store, params IConversationObserver[] observers)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        var compiled = ConfigurationCompiler.CompileAll(
            document,
            new AgentCompilationContext(new FakeChatClientFactory(reply)) { ConversationStore = store })["main"];

        return new ConversationSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            extractor: null,
            observers: observers).Create();
    }

    /// <summary>Starts a streaming turn on a background task and says when the caller can hear it.</summary>
    private static (Task Turn, Task Spoke) StartGatedTurn(ConversationSession session, string userInput)
    {
        TaskCompletionSource spoke = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn = Task.Run(
            async () =>
            {
                await foreach (var _ in session
                    .RunTurnStreamingAsync(userInput, TestContext.Current.CancellationToken)
                    .ConfigureAwait(false))
                {
                    spoke.TrySetResult();
                }
            },
            CancellationToken.None);

        return (turn, spoke.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>Keeps every fact of the conversation, in the order the turn loop raised them.</summary>
    private sealed class RecordingObserver : IConversationObserver
    {
        private readonly Lock _gate = new();
        private readonly List<ConversationEvent> _events = [];

        public IReadOnlyList<ConversationEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public ValueTask OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _events.Add(conversationEvent);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Reads the world at the moment one kind of fact is raised.
    /// </summary>
    /// <remarks>
    /// The dispatcher runs an observer that completes at once on the caller's thread, so what this
    /// reads is what was true when the turn loop raised the fact, and not what became true later.
    /// </remarks>
    private sealed class WatchingObserver : IConversationObserver
    {
        private readonly ConversationEventKind _kind;
        private readonly Action<ConversationEvent> _look;

        public WatchingObserver(ConversationEventKind kind, Action<ConversationEvent> look)
        {
            _kind = kind;
            _look = look;
        }

        public ValueTask OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken)
        {
            if (conversationEvent.Kind == _kind)
            {
                _look(conversationEvent);
            }

            return ValueTask.CompletedTask;
        }
    }
}

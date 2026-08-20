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
/// What store 1 owes the chain: the words first, and a dropped write that costs the call nothing.
/// </summary>
/// <remarks>
/// The chain stores a hash of the spoken text and store 1 stores the text. That split only works in
/// one order — the words have to be there before the row that names their digest — and it only works
/// if a store that refuses a write cannot end the call it is recording.
/// </remarks>
public sealed class CallSessionStoreFailureTests
{
    private const string OneAgentYaml = """
        apiVersion: agentcore/v1
        name: store-failure-check
        agents:
          items:
            - { id: only, instructions: "greet the caller" }
        """;

    /// <summary>
    /// The <c>reply.interrupted</c> row names a hash of the words the caller heard. Raised before
    /// store 1 holds them, it names a hash of words nothing holds.
    /// </summary>
    [Fact]
    public async Task Interrupt_MidReply_StoreOneHoldsTheWordsBeforeTheInterruptedFactIsRaised()
    {
        // Arrange
        RecordingTranscriptStore store = new();
        using ScriptedChatClient reply = new("Hello", " there", " caller") { GateAfterFirstFragment = true };
        List<string> whenTheFactWasRaised = [];
        WatchingObserver observer = new(
            CallEventKind.ReplyInterrupted,
            callEvent => whenTheFactWasRaised.AddRange(
                store.Live(callEvent.CallId).Select(row => row.Content.Text)));
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

    /// <summary>A store 1 write failure never ends a call.</summary>
    [Fact]
    public async Task Append_StoreThrows_CallContinues()
    {
        // Arrange
        using RequestRecordingChatClient reply = new("hi there", "it ships Friday");
        var session = CreateSession(OneAgentYaml, reply, new ThrowingTranscriptStore());
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
    /// A dropped write is a fact about the system and never about the call, so it is counted and
    /// logged and stored nowhere.
    /// </summary>
    [Fact]
    public async Task Append_StoreThrows_RaisesDiagnosticWithNoOrdinal()
    {
        // Arrange
        using RequestRecordingChatClient reply = new("hi there");
        RecordingObserver observer = new();
        var session = CreateSession(OneAgentYaml, reply, new ThrowingTranscriptStore(), observer);

        // Act
        _ = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        // Assert
        await session.FlushTranscriptAsync();
        var dropped = Assert.Single(
            observer.Events,
            callEvent => callEvent.Kind == CallEventKind.TranscriptWriteFailed);
        Assert.Null(dropped.Ordinal);
        Assert.Equal(0, dropped.TurnIndex);

        // The numbers the chain holds are the ones it would have held with no failure at all.
        Assert.Equal(
            [0, 1],
            observer.Events.Where(item => item.Ordinal is not null).Select(item => item.Ordinal));
    }

    private static CallSession CreateSession(
        string yaml, IChatClient reply, ITranscriptStore store, params ICallObserver[] observers)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(new FakeChatClientFactory(reply)) { TranscriptStore = store });

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            extractor: null,
            observers: observers).Create();
    }

    /// <summary>Starts a streaming turn on a background task and says when the caller can hear it.</summary>
    private static (Task Turn, Task Spoke) StartGatedTurn(CallSession session, string userInput)
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

    /// <summary>Keeps every fact of the call, in the order the turn loop raised them.</summary>
    private sealed class RecordingObserver : ICallObserver
    {
        private readonly Lock _gate = new();
        private readonly List<CallEvent> _events = [];

        public IReadOnlyList<CallEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public ValueTask OnCallEventAsync(CallEvent callEvent, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _events.Add(callEvent);
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
    private sealed class WatchingObserver : ICallObserver
    {
        private readonly CallEventKind _kind;
        private readonly Action<CallEvent> _look;

        public WatchingObserver(CallEventKind kind, Action<CallEvent> look)
        {
            _kind = kind;
            _look = look;
        }

        public ValueTask OnCallEventAsync(CallEvent callEvent, CancellationToken cancellationToken)
        {
            if (callEvent.Kind == _kind)
            {
                _look(callEvent);
            }

            return ValueTask.CompletedTask;
        }
    }
}

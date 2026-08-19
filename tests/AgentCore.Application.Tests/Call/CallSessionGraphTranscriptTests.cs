using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Call;

/// <summary>
/// Pins what a graph row keeps and what it sends: the caller-facing turn, and nothing else.
/// </summary>
/// <remarks>
/// A workflow takes no chat history provider and no session of ours, so the turn loop carries the
/// call itself. Every test here runs offline, with no network call and no API key.
/// </remarks>
public sealed class CallSessionGraphTranscriptTests
{
    /// <summary>What the first node says while it works. The caller never hears it.</summary>
    private const string Thinking = "Let me check the order system.";

    /// <summary>What the answering node says. This is the whole spoken reply.</summary>
    private const string Spoken = "Order 41 ships Friday.";

    private const string GraphYaml =
        """
        apiVersion: agentcore/v1
        name: graph-transcript
        agents:
          items:
            - { id: researcher, model: { ref: researcher }, instructions: "look things up" }
            - { id: responder,  model: { ref: responder },  instructions: "answer the caller" }
        graph:
          pattern: sequential
          agents: [ researcher, responder ]
        """;

    /// <summary>
    /// Store 1 holds what the caller said and what the caller heard. The graph's node-to-node
    /// chatter is neither, so it never enters the record.
    /// </summary>
    [Fact]
    public async Task Run_GraphRow_StoresOnlyCallerFacingMessages()
    {
        // Arrange
        RecordingCallMessageStore store = new();
        RecordingNodeClient researcher = new(Thinking);
        RecordingNodeClient responder = new(Spoken);
        var session = CreateSession(researcher, responder, store);

        // Act
        _ = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

        // Assert
        await session.FlushTranscriptAsync();
        Assert.Equal(
            ["where is my order", Spoken],
            store.Live(session.CallId).Select(row => row.Content.Text));
    }

    /// <summary>
    /// The next turn sends the caller-facing history and never the deliberation that produced the
    /// answer, so a node reads only what the caller heard.
    /// </summary>
    [Fact]
    public async Task Run_GraphRow_SecondTurn_ModelSeesNoIntermediateNodeReply()
    {
        // Arrange
        RecordingNodeClient researcher = new(Thinking);
        RecordingNodeClient responder = new(Spoken);
        var session = CreateSession(researcher, responder, new RecordingCallMessageStore());
        _ = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

        // Act
        _ = await session.RunTurnAsync("and the second one?", TestContext.Current.CancellationToken);

        // Assert. The demotion of an assistant message to user is why the call rides one system
        // message: the node reads who said what off the line, not off the role.
        var second = researcher.Requests[1];
        Assert.Contains(
            second,
            message => message.Role == ChatRole.System
                && message.Text.Contains(
                    CallSession.HistoryPreamble + CallSession.CallerLinePrefix + "where is my order",
                    StringComparison.Ordinal)
                && message.Text.Contains(CallSession.AgentLinePrefix + Spoken, StringComparison.Ordinal));
        Assert.DoesNotContain(second, message => message.Text.Contains(Thinking, StringComparison.Ordinal));
        Assert.Contains(second, message => message.Role == ChatRole.User && message.Text == "and the second one?");
    }

    /// <summary>
    /// <c>AgentResponse.Text</c> concatenates every node's reply, so the spoken words are the last
    /// message and never the whole response.
    /// </summary>
    [Fact]
    public async Task Run_GraphRow_ReplyIsFinalNodeOnly()
    {
        // Arrange
        RecordingCallMessageStore store = new();
        RecordingNodeClient researcher = new(Thinking);
        RecordingNodeClient responder = new(Spoken);
        var session = CreateSession(researcher, responder, store);

        // Act
        var turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(Spoken, turn.ReplyText);
        Assert.DoesNotContain(Thinking, turn.ReplyText, StringComparison.Ordinal);
    }

    /// <summary>
    /// R4 on a graph row: the record holds the words the caller heard, and never the tail the
    /// answering node went on to produce.
    /// </summary>
    [Fact]
    public async Task Interrupt_MidGraphReply_StoresTheHeardWordsOnly()
    {
        // Arrange
        RecordingCallMessageStore store = new();
        RecordingNodeClient researcher = new(Thinking);
        using ScriptedChatClient responder = new("Order 41 ", "ships Friday.") { GateAfterFirstFragment = true };
        var session = CreateSession(researcher, responder, store);
        var (turn, spoke) = StartGatedTurn(session, "where is my order");
        await spoke;

        // Act
        var recorded = session.Interrupt("Order 41", TimeSpan.FromMilliseconds(300));

        // Assert
        Assert.True(recorded);
        responder.OpenGate();
        await turn;
        await session.FlushTranscriptAsync();
        Assert.Equal(
            ["where is my order", "Order 41"],
            store.Live(session.CallId).Select(row => row.Content.Text));
    }

    /// <summary>
    /// A cut inside the first 100 ms leaves no heard words, and an empty assistant message teaches
    /// the next turn nothing. The utterance still enters the record alone.
    /// </summary>
    [Fact]
    public async Task Interrupt_BeforeAnyGraphWordLanded_StoresTheCallerUtteranceAlone()
    {
        // Arrange
        RecordingCallMessageStore store = new();
        RecordingNodeClient researcher = new(Thinking);
        using ScriptedChatClient responder = new("Order 41 ", "ships Friday.") { GateAfterFirstFragment = true };
        var session = CreateSession(researcher, responder, store);
        var (turn, spoke) = StartGatedTurn(session, "where is my order");
        await spoke;

        // Act
        var recorded = session.Interrupt(string.Empty, TimeSpan.FromMilliseconds(40));

        // Assert
        Assert.True(recorded);
        responder.OpenGate();
        await turn;
        await session.FlushTranscriptAsync();
        Assert.Equal(["where is my order"], store.Live(session.CallId).Select(row => row.Content.Text));
    }

    /// <summary>Starts a streaming turn on a background task and says when the caller can hear it.</summary>
    /// <param name="session">The call to run the turn on.</param>
    /// <param name="userInput">What the caller said.</param>
    /// <returns>The running turn, and a task that completes at its first spoken update.</returns>
    /// <remarks>
    /// A run that has handed the host nothing is not the turn the caller is hearing, so a barge-in
    /// before the first update takes the amendment path instead and records nothing against this
    /// turn. Waiting for that update is what makes the cut land in the reply.
    /// </remarks>
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

    private static CallSession CreateSession(
        IChatClient researcher, IChatClient responder, ICallMessageStore store)
    {
        var document = ConfigurationLoader.LoadYaml(GraphYaml);
        RoutingChatClientFactory chatClients = new(researcher);
        chatClients.Route("responder", responder);

        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients) { MessageStore = store });

        var factory = new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            extractor: null);

        return factory.Create();
    }

    /// <summary>Answers with one fixed line, and keeps every request it was handed.</summary>
    private sealed class RecordingNodeClient : IChatClient
    {
        private readonly string _reply;

        public RecordingNodeClient(string reply) => _reply = reply;

        /// <summary>Gets what this node's model was sent, one entry per run.</summary>
        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            Record(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            Record(messages);
            await Task.Yield();

            var responseId = Guid.NewGuid().ToString("N");
            yield return new ChatResponseUpdate(ChatRole.Assistant, _reply)
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private void Record(IEnumerable<ChatMessage> messages)
        {
            lock (Requests)
            {
                Requests.Add([.. messages]);
            }
        }
    }
}

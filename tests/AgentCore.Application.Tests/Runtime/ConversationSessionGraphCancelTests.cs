using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Domain;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Agents.Graph;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// On a graph row, a caller cancel is a cancel and a model timeout is a fault.
    /// MAF's workflow agent throws for neither; it reports a fault as an update and ends its stream quietly on
    /// a cancel, so <see cref="GraphFaultAgent"/> has to raise both.
    /// </summary>
    public sealed class ConversationSessionGraphCancelTests
    {
        private const string SequentialYaml =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: first, instructions: "one", model: { ref: first } }
              - { id: second, instructions: "two", model: { ref: second } }
          entries:
            main:
              graph:
                pattern: sequential
                agents: [ first, second ]
          """;

        private const string ExplicitYaml =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: first, instructions: "one", model: { ref: first } }
              - { id: second, instructions: "two", model: { ref: second } }
          entries:
            main:
              graph:
                nodes:
                  - { id: start,  agent: first,  start: true }
                  - { id: finish, agent: second, output: true }
                edges:
                  - { from: start, to: finish }
          """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>
        /// HttpClient reports a request timeout as <see cref="TaskCanceledException"/> even when
        /// nobody cancelled the turn. That must still reach the turn as a run fault that speaks the fallback,
        /// not escape as a cancel.
        /// </summary>
        [Fact]
        public async Task Fault_GraphNodeTimeout_SpeaksTheFallback_InsteadOfEscapingAsACancel()
        {
            ConversationSession session = GraphConversationSessions.Create(SequentialYaml, new TimesOutOnceModel(), new DownModelChatClient(), static _ => null, Ct);

            TurnResult turn = await session.RunTurnAsync("go", Ct);

            Assert.StartsWith(TurnFailureReasons.RunFault, turn.Failure, StringComparison.Ordinal);
            Assert.Equal(AgentCoreConfiguration.DefaultFallbackReply, turn.ReplyText);
        }

        /// <summary>
        /// Control: a real caller cancel must still propagate as a cancel, and the stopped turn must not
        /// keep the fallback line as its reply. A cancel is not a fault, so nothing spoke that line.
        /// </summary>
        [Theory]
        [InlineData(SequentialYaml)]
        [InlineData(ExplicitYaml)]
        public async Task Fault_ARealCallerCancel_StillPropagatesAsACancel(string yaml)
        {
            using HangUntilCancelledChatClient first = new();
            ConversationSession session = GraphConversationSessions.Create(yaml, first, new DownModelChatClient(), static _ => null, Ct);
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

            Task<TurnResult> run = session.RunTurnAsync("go", cts.Token);
            await first.Started.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            cts.Cancel();

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.NotNull(session.LastTurn);
            Assert.Null(session.LastTurn.Failure);
            Assert.Empty(session.LastTurn.ReplyText);
        }

        /// <summary>
        /// The same behaviour one layer down, with no turn loop above it. The turn loop's own notice reader also
        /// watches the caller's token and can throw first, which hid this in the session test about two runs in
        /// three; here nothing but <see cref="GraphFaultAgent"/> can turn MAF's quiet end into a cancel.
        /// The cancel check runs the same way whether the row drains the inner stream or throws at the first
        /// fault, so this must hold on both.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task GraphFaultAgent_CallerCancelDuringANode_ThrowsACancel(bool drain)
        {
            using HangUntilCancelledChatClient model = new();
            AIAgent workflow = AgentWorkflowBuilder
                .BuildSequential("g", [new ChatClientAgent(model, new ChatClientAgentOptions { Name = "first" })])
                .AsAIAgent(name: "g");
            GraphFaultAgent agent = new(workflow, drain);
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

            Task run = DrainAsync(agent.RunStreamingAsync("go", cancellationToken: cts.Token));
            await model.Started.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            cts.Cancel();

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }

        private static async Task DrainAsync(IAsyncEnumerable<AgentResponseUpdate> updates)
        {
            await foreach (AgentResponseUpdate _ in updates)
            {
            }
        }

        /// <summary>A model that throws a timeout-shaped <see cref="TaskCanceledException"/> on its first call, as HttpClient does when nobody cancelled.</summary>
        private sealed class TimesOutOnceModel : IChatClient
        {
            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                throw new TaskCanceledException("HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException());
#pragma warning disable CS0162
                yield break;
#pragma warning restore CS0162
            }

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                => throw new TaskCanceledException("HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException());

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}

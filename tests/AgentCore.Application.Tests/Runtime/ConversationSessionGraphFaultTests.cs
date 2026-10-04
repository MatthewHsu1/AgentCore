using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Agents.Graph;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.ToolCalls;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// A graph node's fault must reach the turn, on both graph rows.
    /// <c>AsAIAgent()</c> reports a node fault as an <see cref="ErrorContent"/> update and never throws,
    /// so without <see cref="GraphFaultAgent"/> the turn read an empty reply and lost the tool-versus-run
    /// distinction <see cref="ToolFaultMark"/> carries.
    /// </summary>
    public sealed class ConversationSessionGraphFaultTests
    {
        private const string SequentialToolYaml =
            """
          apiVersion: agentcore/v1
          tools:
            - { id: price_lookup, kind: builtin, uses: orders.read, description: "Look up the price of an item." }
          agents:
            items:
              - { id: first, instructions: "quote", tools: [ price_lookup ], model: { ref: first } }
              - { id: second, instructions: "two", model: { ref: second } }
          entries:
            main:
              graph:
                pattern: sequential
                agents: [ first, second ]
          """;

        private const string ExplicitToolYaml =
            """
          apiVersion: agentcore/v1
          tools:
            - { id: price_lookup, kind: builtin, uses: orders.read, description: "Look up the price of an item." }
          agents:
            items:
              - { id: first, instructions: "quote", tools: [ price_lookup ], model: { ref: first } }
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

        private const string TodosYaml =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: first, instructions: "track todos", todos: true, model: { ref: first } }
              - { id: second, instructions: "echo back", model: { ref: second } }
          entries:
            main:
              graph:
                pattern: sequential
                agents: [ first, second ]
          """;

        private const string ConcurrentNoStateYaml =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: slow, instructions: "take your time", model: { ref: slow } }
              - { id: broken, instructions: "fail", model: { ref: broken } }
          entries:
            main:
              graph:
                pattern: concurrent
                agents: [ slow, broken ]
          """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Theory]
        [InlineData(SequentialToolYaml)]
        [InlineData(ExplicitToolYaml)]
        public async Task Fault_ToolFaultInsideAGraphNode_IsStillAToolFault(string yaml)
        {
            using CallForever first = new();
            ConversationSession session = GraphConversationSessions.Create(yaml, first, new DownModelChatClient(), ThrowingToolBuilder.Create, Ct);

            TurnResult turn = await session.RunTurnAsync("go", Ct);

            Assert.StartsWith(TurnFailureReasons.ToolFailure, turn.Failure, StringComparison.Ordinal);

            // Not just the right bucket: the tool's own fault, not RequireOutputAgent's unrelated
            // "produced no text" message, which an explicit graph with no GraphFaultAgent throws too
            // and would otherwise pass this assertion for the wrong reason.
            Assert.Contains("the tool is down.", turn.Failure, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(SequentialToolYaml)]
        [InlineData(ExplicitToolYaml)]
        public async Task Fault_ModelDownInsideAGraphNode_IsARunFault(string yaml)
        {
            ConversationSession session = GraphConversationSessions.Create(yaml, new DownModelChatClient(), new DownModelChatClient(), ThrowingToolBuilder.Create, Ct);

            TurnResult turn = await session.RunTurnAsync("go", Ct);

            Assert.StartsWith(TurnFailureReasons.RunFault, turn.Failure, StringComparison.Ordinal);

            // Same reason as above: the model's own fault must reach the turn, not RequireOutputAgent's
            // unrelated "produced no text" message.
            Assert.Contains("503 from the model endpoint", turn.Failure, StringComparison.Ordinal);
        }

        /// <summary>
        /// A concurrent graph whose agents keep no harness state never reuses its workflow
        /// session (<see cref="ConversationSession.ReusesGraphSession"/> is false), so <see cref="GraphFaultAgent"/>
        /// must throw at the broken node's first fault instead of draining the still-open slow node. The slow
        /// node's gate is never opened before the assertion, so a regression that waits for it would time out
        /// here rather than merely run slow.
        /// </summary>
        [Fact]
        public async Task Fault_ConcurrentGraphWithNoReusedSession_SpeaksTheFallbackWithoutWaitingForTheSlowNode()
        {
            using GatedChatClient slow = new(new ScriptedChatClient("slow answer"));
            slow.Arm();
            using DownModelChatClient broken = new();
            RoutingChatClientFactory clients = new(slow);
            _ = clients.Route("slow", slow);
            _ = clients.Route("broken", broken);

            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(ConcurrentNoStateYaml);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(document, new AgentCompilationContext(clients))["main"];
            await using ConversationSession session = new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards)).Create();

            try
            {
                TurnResult turn = await session.RunTurnAsync("go", Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);

                Assert.Equal(AgentCoreConfiguration.DefaultFallbackReply, turn.ReplyText);
            }
            finally
            {
                slow.Open.TrySetResult();
            }
        }

        /// <summary>
        /// A graph row that reuses its workflow session must not send a faulted turn's input into
        /// the next turn. <c>GraphFaultAgent</c> must let MAF's stream finish (so <c>AddMessages</c> and
        /// <c>UpdateBookmark</c> run) before it throws.
        /// </summary>
        [Fact]
        public async Task Fault_ReusedGraphSession_AfterANodeFault_TheNextTurnDoesNotReplayTheFaultedInput()
        {
            RequestCapturingChatClient first = new(new FailsOnceThenAnswersModel());
            ConversationSession session = GraphConversationSessions.Create(TodosYaml, first, new DownModelChatClient(), static _ => null, Ct);

            TurnResult turnOne = await session.RunTurnAsync("zebra first input", Ct);
            Assert.NotNull(turnOne.Failure);

            _ = await session.RunTurnAsync("second input", Ct);

            IReadOnlyList<ChatMessage> turnTwoRequest = first.Requests[^1];
            Assert.DoesNotContain(
                turnTwoRequest,
                message => message.Role == ChatRole.User && message.Text.Contains("zebra first input", StringComparison.Ordinal));
        }

        /// <summary>
        /// MAF documents <see cref="ExecutorFailedEvent.Data"/> as possibly null. Without a
        /// name-the-node fallback, that event's <see cref="ErrorContent"/> passes through unread and the turn
        /// sees an empty reply.
        /// </summary>
        [Fact]
        public async Task Fault_ExecutorFailedEventWithNoException_ThrowsNamingTheFailedNode()
        {
            GraphFaultAgent agent = new(new RawEventAgent(new ExecutorFailedEvent("blocked-node", null)), drain: true);

            InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (AgentResponseUpdate _ in agent.RunStreamingAsync("hi", cancellationToken: Ct))
                {
                }
            });

            Assert.Contains("blocked-node", thrown.Message, StringComparison.Ordinal);
        }

        /// <summary>A tool that always throws. Every call is one of the turn's own retries, not a new turn.</summary>
        private static class ThrowingToolBuilder
        {
            public static AIFunction? Create(ToolConfiguration tool)
            {
                ArgumentNullException.ThrowIfNull(tool);
                return AIFunctionFactory.Create(Fail, tool.Id, tool.Description ?? tool.Id);
            }

            private static string Fail()
            {
                throw new TimeoutException("the tool is down.");
            }
        }
    }
}

using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// Bug 7 of the 2026-09-24 round: <c>DelegatedAgentRun</c> made a fresh session per <c>kind: agent</c>
    /// call and never released it, so a <c>background:</c> child the delegate started outlived the
    /// conversation. The delegate's own <c>ChatOptions</c> attaches its <see cref="Microsoft.Agents.AI.BackgroundAgentsProvider"/>
    /// directly, so the fresh session's only owner is the delegation call itself — it must release it when
    /// the tool returns, on the happy path and on a fault.
    /// </summary>
    public sealed class AgentDelegationBackgroundReleaseTests
    {
        private const string Yaml =
            """
          apiVersion: agentcore/v1
          tools:
            - { id: ask_helper, kind: agent, agent: helper, description: "ask the helper" }
          agents:
            items:
              - { id: first, instructions: "ask", tools: [ ask_helper ], model: { ref: first } }
              - { id: helper, instructions: "delegate work", model: { ref: helper }, background: [ blocker ] }
              - { id: blocker, instructions: "work forever", model: { ref: blocker } }
          entries:
            main:
              agent: first
          """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static Dictionary<string, object?> StartArgs() => new(StringComparer.Ordinal)
        {
            ["agentName"] = "blocker",
            ["input"] = "work",
            ["description"] = "d",
        };

        [Fact]
        public async Task Delegation_ToAnAgentWithABackgroundChild_ReleasesItWhenTheToolReturns()
        {
            using HangUntilCancelledChatClient blocker = new();
            ToolCallingChatClient first = new("first done", new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "go" });
            ToolCallingChatClient helper = new("helper done", StartArgs());
            RoutingChatClientFactory clients = new(first);
            _ = clients.Route("first", first);
            _ = clients.Route("helper", helper);
            _ = clients.Route("blocker", blocker);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(Yaml),
                new AgentCompilationContext(clients) { ConversationStore = new InMemoryConversationStore() })["main"];
            ConversationSession session = new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards)).Create("c1");

            TurnResult turn = await session.RunTurnAsync("go", Ct);

            Assert.Equal("first done", turn.ReplyText);
            await blocker.Started.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await blocker.Cancelled.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        /// <summary>
        /// Claim (R4-5): a delegation whose fresh session started a background child that ignores its cancel
        /// must not stall the outer turn for <see cref="Harness.BackgroundSessionRelease"/>'s timeout. Before
        /// the fix, the delegation's <c>finally</c> awaited the release with <see cref="CancellationToken.None"/>
        /// inside the tool call, so the outer model never saw the tool result until the release gave up.
        /// </summary>
        [Fact]
        public async Task Delegation_ToAnAgentWithADeafChild_DoesNotStallTheTurn()
        {
            using DeafChatClient blocker = new();
            ToolCallingChatClient first = new("first done", new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "go" });
            ToolCallingChatClient helper = new("helper done", StartArgs());
            RoutingChatClientFactory clients = new(first);
            _ = clients.Route("first", first);
            _ = clients.Route("helper", helper);
            _ = clients.Route("blocker", blocker);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(Yaml),
                new AgentCompilationContext(clients) { ConversationStore = new InMemoryConversationStore() })["main"];
            ConversationSession session = new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards)).Create("c1");

            TurnResult turn = await session.RunTurnAsync("go", Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);

            Assert.Equal("first done", turn.ReplyText);
            blocker.Release();
        }
    }
}

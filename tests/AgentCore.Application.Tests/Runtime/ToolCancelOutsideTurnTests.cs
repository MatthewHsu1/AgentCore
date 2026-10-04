using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Hooks;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Runtime
{
    // Only a conversation turn hands its running tools the end backstop in place of the caller's token.
    public sealed class ToolCancelOutsideTurnTests
    {
        private const string BackgroundYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: price_lookup, kind: builtin, uses: orders.read, description: "Look up the price of an item." }
        agents:
          defaults: { clock: false }
          items:
            - { id: parent, instructions: "delegate work", model: { ref: parent }, background: [ worker ] }
            - { id: worker, instructions: "quote the price", model: { ref: worker }, tools: [ price_lookup ] }
        entries:
          main:
            agent: parent
        """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact(Timeout = 20_000)]
        public async Task ARunOutsideATurnStillCancelsItsRunningToolWhenTheCallerCancels()
        {
            GatedTool tool = new();
            AIAgent agent = HookSessions.Compile(
                HookSessions.ToolAgentYaml,
                new CueToolChatClient("F63", () => [], "ok"),
                tools: declared => tool.Create(declared.Id, declared.Description ?? declared.Id))["main"].Agents["only"];
            AgentSession session = await agent.CreateSessionAsync(Ct);
            using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

            Task<AgentResponse> run = agent.RunAsync("Max speed of the F63?", session, cancellationToken: caller.Token);
            await tool.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await caller.CancelAsync();

            Task first = await Task.WhenAny(tool.Cancelled.Task, Task.Delay(TimeSpan.FromSeconds(3), Ct));
            bool cancelled = first == tool.Cancelled.Task;
            tool.Release.TrySetResult();
            try
            {
                _ = await run;
            }
            catch (OperationCanceledException)
            {
            }

            Assert.True(cancelled, "the caller cancelled the run, yet the running tool was never cancelled");
        }

        // A background child outlives its turn, so its tool reads its own run's token, and the end backstop as well.
        [Fact(Timeout = 20_000)]
        public async Task ABackgroundChildsRunningToolIsCancelledThirtySecondsAfterTheConversationEnds()
        {
            DateTimeOffset start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
            FakeTimeProvider time = new(start);
            GatedTool tool = new();
            ToolCallingChatClient parent = new(
                "started",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["agentName"] = "worker",
                    ["input"] = "F63",
                    ["description"] = "d",
                });
            RoutingChatClientFactory models = new(parent);
            _ = models.Route("parent", parent);
            _ = models.Route("worker", new CueToolChatClient("F63", () => [], "done"));
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(BackgroundYaml);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(models)
                {
                    Tools = TestToolRegistry.From(document, declared => tool.Create(declared.Id, declared.Description ?? declared.Id), Ct),
                    Clock = time,
                })["main"];
            ConversationSession session = new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards), timeProvider: time)
                .Create("conversation-1");

            _ = await session.RunTurnAsync("go", Ct);
            await tool.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            _ = session.EndConversation(ConversationEndReason.CallerHungUp);
            await time.WaitForTimersAsync(start + ConversationEnding.ToolGrace, 1).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            time.Advance(ConversationEnding.ToolGrace - TimeSpan.FromSeconds(1));
            Assert.False(tool.Cancelled.Task.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(1));

            Task first = await Task.WhenAny(tool.Cancelled.Task, Task.Delay(TimeSpan.FromSeconds(3), Ct));
            bool cancelled = first == tool.Cancelled.Task;
            tool.Release.TrySetResult();
            await session.DisposeAsync();

            Assert.True(cancelled, "the conversation ended 30 s ago, yet the background child's tool still ran");
        }
    }
}

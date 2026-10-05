using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Sessions
{
    /// <summary>The one-agent session factory and the fixed clock the session store tests run on.</summary>
    internal static class ConversationSessionsFixture
    {
        private const string Document = """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            agent: only
        """;

        internal static CancellationToken Token => TestContext.Current.CancellationToken;

        internal static FakeTimeProvider Clock()
        {
            return new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        }

        internal static ConversationSessionFactory Factory(
            IConversationStore? transcript = null,
            IChatClient? reply = null,
            AgentHook? hook = null,
            string? workspaceRoot = null,
            TimeProvider? timeProvider = null)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(Document);
            RoutingChatClientFactory chatClients = new(reply ?? new ScriptedChatClient("hello"));
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients) { ConversationStore = transcript, WorkspaceRoot = workspaceRoot })[SingleEntrySessionFactories.MainEntry];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                ConversationSessionFactory.CreateExtractor(compiled, chatClients),
                timeProvider: timeProvider,
                workspaceRoot: workspaceRoot,
                hooks: hook is null ? null : [hook]);
        }

        /// <summary>Waits for a condition that background work makes true.</summary>
        /// <param name="condition">The condition.</param>
        /// <returns>A task that completes once the condition holds.</returns>
        /// <exception cref="TimeoutException">The condition did not hold within ten seconds.</exception>
        internal static async Task EventuallyAsync(Func<bool> condition)
        {
            using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(Token);
            bound.CancelAfter(TimeSpan.FromSeconds(10));

            while (!condition())
            {
                try
                {
                    await Task.Delay(10, bound.Token);
                }
                catch (OperationCanceledException) when (!Token.IsCancellationRequested)
                {
                    throw new TimeoutException("The condition did not hold within ten seconds.");
                }
            }
        }
    }
}

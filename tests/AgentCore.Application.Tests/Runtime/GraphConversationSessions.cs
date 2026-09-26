using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>Compiles the <c>main</c> entry of a graph document into a conversation session.</summary>
    internal static class GraphConversationSessions
    {
        /// <summary>Builds a session whose agents <c>first</c> and <c>second</c> talk to the given models.</summary>
        /// <param name="yaml">A document whose <c>main</c> entry is a graph over agents named <c>first</c> and <c>second</c>.</param>
        /// <param name="first">The model of the agent <c>first</c>.</param>
        /// <param name="second">The model of the agent <c>second</c>.</param>
        /// <param name="tools">Builds each <c>tools:</c> entry, or returns <see langword="null"/> to leave it unbuilt.</param>
        /// <param name="cancellationToken">The test's token, for the tool registry.</param>
        public static ConversationSession Create(
            string yaml, IChatClient first, IChatClient second, Func<ToolConfiguration, AITool?> tools, CancellationToken cancellationToken)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            RoutingChatClientFactory clients = new(first);
            _ = clients.Route("first", first);
            _ = clients.Route("second", second);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(clients) { Tools = TestToolRegistry.From(document, tools, cancellationToken) })["main"];
            return new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards)).Create();
        }
    }
}

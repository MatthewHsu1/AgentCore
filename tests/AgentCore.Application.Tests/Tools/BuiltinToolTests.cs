using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tests.Configuration;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tools.Registry;
using AgentCore.Application.Tools.Builtin;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools
{
    /// <summary>
    /// The first tool kind of section 8.1: <c>kind: builtin</c>, which AgentCore ships.
    /// </summary>
    /// <remarks>
    /// The shipped example holds two built-ins: <c>web.search</c>, a marker that builds one
    /// <c>HostedWebSearchTool</c> and runs no code of its own — see <c>HostedWebSearchDropTests</c> for
    /// what reaches a compiled agent — and <c>file.publish</c>, a plain function whose conversation path is
    /// covered in <c>FilePublishToolDefinitionTests</c>.
    /// </remarks>
    public sealed class BuiltinToolTests
    {
        // ---------------------------------------------------------------------------------------------
        // Binding the name.
        // ---------------------------------------------------------------------------------------------
        [Fact]
        public void TheWorkedExample_BindsEveryBuiltInName()
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);
            BuiltinFactory factory = Factory();

            foreach (ToolConfiguration? tool in document.Tools.Where(tool => tool.Kind == ToolKind.Builtin))
            {
                Assert.NotNull(factory.Create(tool));
            }
        }

        [Fact]
        public void AUsesNameNobodyShips_FailsAtStartup()
        {
            ToolConfiguration tool = new() { Id = "x", Kind = ToolKind.Builtin, Uses = "knowledge.summarise" };

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(() => Factory().Create(tool));

            Assert.Equal(ConfigurationCheck.ReferenceResolution, failure.Check);
            Assert.Contains("knowledge.summarise", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheBuiltinFactory_ServesNoOtherKind()
        {
            Assert.Null(Factory().Create(new ToolConfiguration { Id = "other", Kind = ToolKind.Binding, Binds = "X" }));
            Assert.Null(Factory().Create(new ToolConfiguration { Id = "inner", Kind = ToolKind.Agent, Agent = "a" }));
        }

        [Fact]
        public async Task AUsesNameAgentCoreDoesNotShip_FailsTheBoot()
        {
            BuiltinToolSource source = new(new BuiltinToolPorts(null));
            ToolSourceContext context = new(new AgentCoreConfiguration
            {
                ApiVersion = "agentcore/v1",
                Agents = new AgentsConfiguration { Items = [] },
                Entries = new Dictionary<string, EntryConfiguration>(),
                Tools = [new ToolConfiguration { Id = "x", Kind = ToolKind.Builtin, Uses = "knowledge.invent" }],
            });

            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(async () =>
                await source.ProvideAsync(context, TestContext.Current.CancellationToken));

            Assert.Contains("knowledge.invent", failure.Message, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------------
        private static BuiltinFactory Factory()
        {
            return new();
        }

        /// <summary>Builds one declared tool through <see cref="BuiltinToolSource"/>, synchronously.</summary>
        private sealed class BuiltinFactory
        {
            private readonly BuiltinToolSource _source = new(new BuiltinToolPorts(
                new RecordingChatClientFactory(),
                new RecordingBlobStore(),
                WorkspaceRoot: Path.Combine(Path.GetTempPath(), "agentcore-builtin-tests")));

            public AITool? Create(ToolConfiguration tool)
            {
                if (tool.Kind != ToolKind.Builtin)
                {
                    return null;
                }

                ToolSourceContext context = new(new AgentCoreConfiguration
                {
                    ApiVersion = "agentcore/v1",
                    Agents = new AgentsConfiguration { Items = [] },
                    Entries = new Dictionary<string, EntryConfiguration>(),
                    Tools = [tool],
                });

                IReadOnlyList<ToolRegistration> registrations = _source.ProvideAsync(context).AsTask().GetAwaiter().GetResult();
                return Assert.Single(registrations).Materialise();
            }
        }
    }
}

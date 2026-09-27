using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Validation
{
    /// <summary>
    /// Check 10 (ruling 6): an agent used as a graph node may not declare <c>background:</c>. MAF 1.21.0
    /// gives a workflow node's session no release hook, so a background child started inside a node would
    /// outlive the conversation.
    /// </summary>
    public sealed class GraphNodeBackgroundCheckTests
    {
        [Fact]
        public void ABackgroundAgentUsedAsAPatternGraphNode_FailsCheckTen()
        {
            const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: parent, background: [ blocker ] }
                - { id: blocker }
                - { id: echo }
            entries:
              main:
                graph:
                  pattern: sequential
                  agents: [ parent, echo ]
            """;

            ConfigurationError error = Assert.Single(Evaluate(document, ConfigurationCheck.GraphNodeBackground));

            Assert.Equal("/agents/items/0/background", error.Pointer);
            Assert.Contains("the agent 'parent' declares background:", error.Message, StringComparison.Ordinal);
            Assert.Contains("entry 'main'", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ABackgroundAgentUsedAsAnExplicitGraphNode_FailsCheckTen()
        {
            const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: parent, background: [ blocker ] }
                - { id: blocker }
                - { id: echo }
            entries:
              main:
                graph:
                  nodes:
                    - { id: start,  agent: parent, start: true }
                    - { id: finish, agent: echo, output: true }
                  edges:
                    - { from: start, to: finish }
            """;

            ConfigurationError error = Assert.Single(Evaluate(document, ConfigurationCheck.GraphNodeBackground));

            Assert.Equal("/agents/items/0/background", error.Pointer);
            Assert.Contains("the agent 'parent' declares background:", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Claim (F8): check 10 must not live only in the validator. Before the fix, the public
        /// <see cref="ConfigurationCompiler.CompileAll"/> built the document the validator above refuses.
        /// </summary>
        [Fact]
        public void ABackgroundAgentUsedAsAPatternGraphNode_AlsoFailsCompileAll()
        {
            const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: parent, background: [ blocker ] }
                - { id: blocker }
                - { id: echo }
            entries:
              main:
                graph:
                  pattern: sequential
                  agents: [ parent, echo ]
            """;

            _ = Assert.Throws<ConfigurationLoadException>(() => ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(document),
                new AgentCompilationContext(new RoutingChatClientFactory(new ScriptedChatClient("x")))));
        }

        /// <summary>Claim (F8), the explicit-graph row.</summary>
        [Fact]
        public void ABackgroundAgentUsedAsAnExplicitGraphNode_AlsoFailsCompileAll()
        {
            const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: parent, background: [ blocker ] }
                - { id: blocker }
                - { id: echo }
            entries:
              main:
                graph:
                  nodes:
                    - { id: start,  agent: parent, start: true }
                    - { id: finish, agent: echo, output: true }
                  edges:
                    - { from: start, to: finish }
            """;

            _ = Assert.Throws<ConfigurationLoadException>(() => ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(document),
                new AgentCompilationContext(new RoutingChatClientFactory(new ScriptedChatClient("x")))));
        }

        [Fact]
        public void ABackgroundAgentUsedOutsideAnyGraph_PassesCheckTen()
        {
            const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: parent, background: [ blocker ] }
                - { id: blocker }
            entries:
              main:
                agent: parent
            """;

            Assert.DoesNotContain(
                ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(document)).Errors,
                error => error.Check == ConfigurationCheck.GraphNodeBackground);
        }

        [Fact]
        public void AGraphNodeAgentWithNoBackground_PassesCheckTen()
        {
            const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: first }
                - { id: second }
            entries:
              main:
                graph:
                  pattern: sequential
                  agents: [ first, second ]
            """;

            Assert.DoesNotContain(
                ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(document)).Errors,
                error => error.Check == ConfigurationCheck.GraphNodeBackground);
        }

        private static IReadOnlyList<ConfigurationError> Evaluate(string yaml, ConfigurationCheck check)
        {
            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(yaml);
            ConfigurationValidationResult result = ConfigurationValidator.Evaluate(configuration);

            Assert.All(result.Errors, error => Assert.Equal(check, error.Check));
            return result.Errors;
        }
    }
}

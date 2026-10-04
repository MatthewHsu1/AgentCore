using System.Text.Json;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Xunit;

namespace AgentCore.Application.Tests.Configuration
{
    /// <summary>
    /// What the loader refuses in the YAML or JSON itself, before any section binds.
    /// </summary>
    public sealed class ConfigurationLoaderParsingTests
    {
        /// <summary>
        /// A repeated key is a mistake in both formats. On the YAML path YamlDotNet's own loader rejects
        /// two keys that are the same YAML node, before <see cref="YamlToJson"/> walks the mapping.
        /// </summary>
        [Fact]
        public void ADuplicateKeyInYaml_FailsTheLoad()
        {
            const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            agents:
              items:
                - { id: only, instructions: "ok" }
            """;

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

            Assert.Contains(failure.Errors, error => error.Message.Contains("Duplicate key", StringComparison.Ordinal));
        }

        /// <summary>
        /// Two keys that differ as YAML and agree as JSON are still one key in the document tree.
        /// </summary>
        [Fact]
        public void TwoKeysThatDifferOnlyByTag_FailTheLoad()
        {
            const string document = """
            apiVersion: agentcore/v1
            !!str 1: first
            1: second
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

            Assert.Equal(ConfigurationCheck.Syntax, failure.Check);
            Assert.Contains(failure.Errors, error => error.Message.Contains("appears twice", StringComparison.Ordinal));
        }

        /// <summary>
        /// A number no <see cref="double"/> can hold is a defect in the document, not a crash.
        /// </summary>
        [Theory]
        [InlineData("1e400")]
        [InlineData("-1e400")]
        public void ANumberTooLargeToHold_FailsTheLoad(string written)
        {
            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(
                () => ConfigurationLoader.LoadYaml($"apiVersion: agentcore/v1\nevaluation:\n  sampleRate: {written}\n"));

            Assert.Equal(ConfigurationCheck.Syntax, failure.Check);
            Assert.Contains(failure.Errors, error => error.Message.Contains("larger than a number can hold", StringComparison.Ordinal));
        }

        /// <summary>A number that underflows is zero, which JSON can write, so it loads.</summary>
        [Fact]
        public void ANumberTooSmallToHold_ReadsAsZero()
        {
            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(
                "apiVersion: agentcore/v1\nevaluation:\n  sampleRate: 1e-400\nagents:\n  items:\n    - { id: only, instructions: \"ok\" }\nentries:\n  main:\n    agent: only\n");

            Assert.Equal(0, configuration.Evaluation!.SampleRate);
        }

        /// <summary>
        /// A repeated key on the JSON path is rejected by <c>ReadJson</c>'s own
        /// <see cref="JsonDocumentOptions.AllowDuplicateProperties"/> setting before the
        /// document ever reaches the shared reparse. This pins that behaviour too.
        /// </summary>
        [Fact]
        public void ADuplicateKeyInJson_FailsTheLoad()
        {
            const string document = /*lang=json,strict*/ """
            {
              "apiVersion": "agentcore/v1",
              "agents": { "items": [{ "id": "only" }] },
              "agents": { "items": [{ "id": "only" }] }
            }
            """;

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadJson(document));

            Assert.Contains(failure.Errors, error => error.Check == ConfigurationCheck.Syntax);
        }
    }
}

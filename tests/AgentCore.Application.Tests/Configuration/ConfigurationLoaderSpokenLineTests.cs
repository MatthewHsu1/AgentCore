using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Xunit;

namespace AgentCore.Application.Tests.Configuration
{
    /// <summary>
    /// The lines a document has the caller hear, and its evaluation section, bind or take their defaults.
    /// </summary>
    public sealed class ConfigurationLoaderSpokenLineTests
    {
        private static readonly AgentCoreConfiguration Example = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);

        [Fact]
        public void Example_BindsTheSpokenFallbackAndTheSampleRate()
        {
            // Both keys are optional, and the worked example writes both at their default.
            Assert.Equal(AgentCoreConfiguration.DefaultFallbackReply, Example.FallbackReply);
            Assert.NotNull(Example.Evaluation);
            Assert.Equal(EvaluationConfiguration.DefaultSampleRate, Example.Evaluation!.SampleRate);
        }

        [Fact]
        public void Example_BindsTheJudgeModelReference()
        {
            Assert.NotNull(Example.Evaluation!.Judge);
            Assert.Equal("judge", Example.Evaluation.Judge!.Ref);
            Assert.Equal(0, Example.Evaluation.Judge.Temperature);
        }

        [Fact]
        public void AnEvaluationSectionWithNoJudge_LeavesTheReferenceNull()
        {
            const string document = """
            apiVersion: agentcore/v1
            evaluation:
              sampleRate: 0.25
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(document);

            Assert.Null(configuration.Evaluation!.Judge);
        }

        [Fact]
        public void AJudgeWithNoRef_FailsTheLoad()
        {
            const string document = """
            apiVersion: agentcore/v1
            evaluation:
              judge: { temperature: 0 }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

            Assert.Contains(failure.Errors, error => error.Pointer == "/evaluation/judge");
        }

        [Fact]
        public void ADocumentThatSetsBothTunableKeys_BindsThem()
        {
            const string document = """
            apiVersion: agentcore/v1
            fallbackReply: "One moment please. I will try that again."
            evaluation:
              sampleRate: 0.25
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(document);

            Assert.Equal("One moment please. I will try that again.", configuration.FallbackReply);
            Assert.Equal(0.25, configuration.Evaluation!.SampleRate);
        }

        [Fact]
        public void ADocumentThatOmitsBothTunableKeys_TakesTheDefaults()
        {
            const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(document);

            // A fallback is still spoken, and no turn is evaluated.
            Assert.Equal(AgentCoreConfiguration.DefaultFallbackReply, configuration.FallbackReply);
            Assert.Null(configuration.Evaluation);
        }

        [Fact]
        public void Example_BindsTheSpokenRefusal()
        {
            // The key is optional, and the worked example writes it at its default.
            Assert.Equal(AgentCoreConfiguration.DefaultRefusalReply, Example.RefusalReply);
        }

        [Fact]
        public void ADocumentThatSetsTheRefusalReply_BindsIt()
        {
            const string document = """
            apiVersion: agentcore/v1
            refusalReply: "I am not able to answer that."
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(document);

            Assert.Equal("I am not able to answer that.", configuration.RefusalReply);
        }

        [Fact]
        public void ADocumentThatOmitsTheRefusalReply_TakesTheDefault()
        {
            const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(document);

            // A document written before the key existed behaves the same way.
            Assert.Equal(AgentCoreConfiguration.DefaultRefusalReply, configuration.RefusalReply);
        }

        [Fact]
        public void ADocumentThatSetsOnlyOneSpokenLine_LeavesTheOtherAtItsDefault()
        {
            AgentCoreConfiguration withFallback = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            fallbackReply: "One moment please. I will try that again."
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """);
            AgentCoreConfiguration withRefusal = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            refusalReply: "I am not able to answer that."
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """);

            Assert.Equal("One moment please. I will try that again.", withFallback.FallbackReply);
            Assert.Equal(AgentCoreConfiguration.DefaultRefusalReply, withFallback.RefusalReply);
            Assert.Equal(AgentCoreConfiguration.DefaultFallbackReply, withRefusal.FallbackReply);
            Assert.Equal("I am not able to answer that.", withRefusal.RefusalReply);
        }

        [Fact]
        public void ADocumentThatSetsBothSpokenLines_BindsThemIndependently()
        {
            const string document = """
            apiVersion: agentcore/v1
            fallbackReply: "One moment please. I will try that again."
            refusalReply: "I am not able to answer that."
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(document);

            Assert.Equal("One moment please. I will try that again.", configuration.FallbackReply);
            Assert.Equal("I am not able to answer that.", configuration.RefusalReply);
        }

        [Fact]
        public void AnEvaluationSectionWithNoRate_TakesTheDefaultRate()
        {
            const string document = """
            apiVersion: agentcore/v1
            evaluation: {}
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(document);

            Assert.Equal(EvaluationConfiguration.DefaultSampleRate, configuration.Evaluation!.SampleRate);
        }

        [Theory]
        [InlineData("0", 0.0)]
        [InlineData("1", 1.0)]
        [InlineData("0.05", 0.05)]
        public void ASampleRateInsideTheRange_Binds(string written, double expected)
        {
            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(
                $"apiVersion: agentcore/v1\nevaluation:\n  sampleRate: {written}\nagents:\n  items:\n    - {{ id: only, instructions: \"ok\" }}\nentries:\n  main:\n    agent: only\n");

            Assert.Equal(expected, configuration.Evaluation!.SampleRate);
        }
    }
}

using AgentCore.Application.Configuration.Parsing;
using Xunit;

namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// Check 1 of section 8.5, and the JSON Pointer that section 8.7 requires of every load failure.
/// </summary>
public sealed class ConfigurationSchemaValidatorTests
{
    [Fact]
    public void TheExample_PassesCheckOne()
    {
        var document = ConfigurationLoader.ReadDocument(ExampleDocument.Yaml, ConfigurationFormat.Yaml);

        Assert.Empty(ConfigurationSchemaValidator.Evaluate(document));
    }

    [Fact]
    public void AnUnknownToolKind_FailsWithThePointerOfThatTool()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            tools:
              - { id: search_chunks, kind: builtin, uses: knowledge.search }
              - { id: lookup_order,  kind: ftp }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer == "/tools/1/kind");
        Assert.Contains("/tools/1/kind", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRequiredProperty_FailsWithThePointerOfItsParent()
    {
        const string document = """
            apiVersion: agentcore/v1
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Equal(ConfigurationError.RootPointer, failure.Pointer);
        Assert.Contains("name", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWrongApiVersion_FailsWithThePointerOfTheField()
    {
        const string document = """
            apiVersion: agentcore/v2
            name: broken
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains(failure.Errors, error => error.Pointer == "/apiVersion");
    }

    [Fact]
    public void ASecondExtractorTrigger_Fails()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            extractor:
              model: { ref: fill }
              when: in_reply
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains(failure.Errors, error => error.Pointer == "/extractor/when");
    }

    [Fact]
    public void AToolWriterWithNoPath_FailsWithThePointerOfTheSlot()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            state:
              orderStatus: { type: string, writer: tool }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains(failure.Errors, error => error.Pointer == "/state/orderStatus");
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void AFallbackReplyWithNoWords_FailsWithThePointerOfTheField(string written)
    {
        // Section 8.7 asks for a spoken fallback, and a line with no words is silence on a call.
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml($"apiVersion: agentcore/v1\nname: broken\nfallbackReply: {written}\n"));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer == "/fallbackReply");
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.1")]
    public void ASampleRateOutsideTheRange_FailsWithThePointerOfTheField(string written)
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml(
                $"apiVersion: agentcore/v1\nname: broken\nevaluation:\n  sampleRate: {written}\n"));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer == "/evaluation/sampleRate");
        Assert.Contains("/evaluation/sampleRate", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASampleRateThatIsNotANumber_FailsWithThePointerOfTheField()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            evaluation:
              sampleRate: "half"
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains(failure.Errors, error => error.Pointer == "/evaluation/sampleRate");
    }

    [Fact]
    public void AnUnknownEvaluationKey_Fails()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            evaluation:
              sampleRatio: 0.5
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains("sampleRatio", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownProperty_Fails()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            stage: greeting
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains("stage", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APolicyAndAGraphTogether_Fail()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            agents:
              items:
                - { id: greeter }
            policy:
              initial: greeting
              stages:
                - { id: greeting, agent: greeter, terminal: true }
            graph:
              pattern: sequential
              agents: [ greeter ]
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
    }

    [Fact]
    public void AGraphWithNodesAndEdges_Loads()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: reviewed
            agents:
              items:
                - { id: writer }
                - { id: reviewer }
            graph:
              nodes:
                - { id: draft,  agent: writer,   start: true }
                - { id: review, agent: reviewer, output: true }
              edges:
                - { from: draft, to: review, when: { var: resolved } }
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        Assert.NotNull(configuration.Graph);
        Assert.Null(configuration.Graph!.Pattern);
        Assert.Equal(2, configuration.Graph.Nodes.Count);
        Assert.True(configuration.Graph.Nodes[0].Start);
        Assert.True(configuration.Graph.Nodes[1].Output);

        var edge = Assert.Single(configuration.Graph.Edges);
        Assert.Equal("draft", edge.From);
        Assert.False(edge.When!.IsNamed);
        Assert.Equal("""{"var":"resolved"}""", edge.When!.Rule!.ToJsonString());
    }

    [Fact]
    public void AGraphWithAPattern_Loads()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: pipeline
            agents:
              items:
                - { id: writer }
                - { id: reviewer }
            graph:
              pattern: group_chat
              agents: [ writer, reviewer ]
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        Assert.Equal(AgentCore.Application.Configuration.Schema.GraphPattern.GroupChat, configuration.Graph!.Pattern);
        Assert.Equal(["writer", "reviewer"], configuration.Graph.Agents);
        Assert.Empty(configuration.Graph.Nodes);
    }

    [Fact]
    public void MalformedYaml_FailsBeforeCheckOne()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml("apiVersion: [agentcore/v1\nname: broken"));

        Assert.Equal(ConfigurationCheck.Syntax, failure.Check);
    }

    [Fact]
    public void ADuplicateKey_FailsBeforeCheckOne()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: first
            name: second
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Equal(ConfigurationCheck.Syntax, failure.Check);
    }

    [Fact]
    public void TheEmbeddedSchema_IsPresent()
        => Assert.Contains("agentcore/v1", ConfigurationSchemaValidator.SchemaJson, StringComparison.Ordinal);
}

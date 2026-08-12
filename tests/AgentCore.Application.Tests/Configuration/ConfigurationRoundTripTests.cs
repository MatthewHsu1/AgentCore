using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using Xunit;

namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// Rule 17 of section 11: the same document loads identically as YAML and as JSON.
/// </summary>
public sealed class ConfigurationRoundTripTests
{
    [Fact]
    public void SameDocument_BindsToEqualRecords()
    {
        var fromYaml = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);
        var fromJson = ConfigurationLoader.LoadJson(ExampleDocument.Json);

        Assert.Equal(fromYaml, fromJson);
        Assert.True(fromYaml == fromJson);
        Assert.Equal(fromYaml.GetHashCode(), fromJson.GetHashCode());
    }

    [Fact]
    public void SameDocument_ReadsToTheSameNodeTree()
    {
        var fromYaml = ConfigurationLoader.ReadDocument(ExampleDocument.Yaml, ConfigurationFormat.Yaml);
        var fromJson = ConfigurationLoader.ReadDocument(ExampleDocument.Json, ConfigurationFormat.Json);

        Assert.True(JsonNode.DeepEquals(fromYaml, fromJson));
    }

    [Fact]
    public void ADifferentDocument_DoesNotBindToEqualRecords()
    {
        var fromYaml = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);
        var changed = ConfigurationLoader.LoadJson(
            ExampleDocument.Json.Replace("\"service-voice\"", "\"other-voice\"", StringComparison.Ordinal));

        Assert.NotEqual(fromYaml, changed);
    }

    [Fact]
    public void AChangedGuardThreshold_DoesNotBindToEqualRecords()
    {
        var fromYaml = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);
        var changed = ConfigurationLoader.LoadYaml(
            ExampleDocument.Yaml.Replace("failedResolveTurns }, 3 ]", "failedResolveTurns }, 4 ]", StringComparison.Ordinal));

        Assert.NotEqual(fromYaml, changed);
    }

    [Fact]
    public void TheTwoTunableKeys_ReadTheSameFromYamlAndFromJson()
    {
        var fromYaml = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            name: tuned
            fallbackReply: "One moment please. I will try that again."
            evaluation:
              sampleRate: 0.25
            """);
        var fromJson = ConfigurationLoader.LoadJson("""
            {
              "apiVersion": "agentcore/v1",
              "name": "tuned",
              "fallbackReply": "One moment please. I will try that again.",
              "evaluation": { "sampleRate": 0.25 }
            }
            """);

        Assert.Equal(fromYaml, fromJson);
        Assert.Equal(fromYaml.GetHashCode(), fromJson.GetHashCode());
    }

    [Fact]
    public void AChangedSampleRate_DoesNotBindToEqualRecords()
    {
        var fromYaml = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);
        var changed = ConfigurationLoader.LoadYaml(
            ExampleDocument.Yaml.Replace("sampleRate: 0", "sampleRate: 0.5", StringComparison.Ordinal));

        Assert.NotEqual(fromYaml, changed);
    }

    [Fact]
    public void AChangedFallbackReply_DoesNotBindToEqualRecords()
    {
        var fromYaml = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);
        var changed = ConfigurationLoader.LoadYaml(
            ExampleDocument.Yaml.Replace("Please say it again.", "Please try once more.", StringComparison.Ordinal));

        Assert.NotEqual(fromYaml, changed);
    }

    [Fact]
    public void ADuplicateJsonKey_Fails()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadJson("""{"apiVersion":"agentcore/v1","name":"first","name":"second"}"""));

        Assert.Equal(ConfigurationCheck.Syntax, failure.Check);
    }

    [Theory]
    [InlineData("value: 3", 3L)]
    [InlineData("value: -7", -7L)]
    [InlineData("value: 0x10", 16L)]
    public void APlainIntegerScalar_ReadsAsANumber(string yaml, long expected)
    {
        var document = ConfigurationLoader.ReadDocument(yaml, ConfigurationFormat.Yaml);

        Assert.Equal(expected, document["value"]!.GetValue<long>());
    }

    [Theory]
    [InlineData("value: true", true)]
    [InlineData("value: FALSE", false)]
    public void APlainBooleanScalar_ReadsAsABoolean(string yaml, bool expected)
    {
        var document = ConfigurationLoader.ReadDocument(yaml, ConfigurationFormat.Yaml);

        Assert.Equal(expected, document["value"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("value: \"3\"", "3")]
    [InlineData("value: 'true'", "true")]
    [InlineData("value: gpt-4.1-mini", "gpt-4.1-mini")]
    [InlineData("value: agentcore/v1", "agentcore/v1")]
    [InlineData("value: ./kb", "./kb")]
    [InlineData("value: yes", "yes")]
    public void AQuotedOrWordScalar_ReadsAsAString(string yaml, string expected)
    {
        var document = ConfigurationLoader.ReadDocument(yaml, ConfigurationFormat.Yaml);

        Assert.Equal(expected, document["value"]!.GetValue<string>());
    }

    [Fact]
    public void APlainFloatScalar_ReadsAsANumber()
    {
        var document = ConfigurationLoader.ReadDocument("value: 0.3", ConfigurationFormat.Yaml);

        Assert.Equal(0.3, document["value"]!.GetValue<double>());
    }
}

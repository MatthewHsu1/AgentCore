using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Validation;

/// <summary>
/// Check 9 over <c>reasoningEffort</c> and <c>temperature</c>.
/// </summary>
/// <remarks>
/// The vendor answers 400 to the pair, and its message names only <c>temperature</c>. These tests
/// pin the load-time error instead, because it names both keys and points at the one the author
/// has to change.
/// </remarks>
public sealed class ReasoningTemperatureCheckTests
{
    [Fact]
    public void AReasoningEntryWithATemperature_FailsAndNamesBothKeys()
    {
        var error = Assert.Single(Evaluate(Document("low", "temperature: 0.2")));

        Assert.Equal("/agents/items/0/model/temperature", error.Pointer);
        Assert.Contains("temperature 0.2", error.Message, StringComparison.Ordinal);
        Assert.Contains("reasoningEffort 'low'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'reply'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void EveryEffortAboveNone_RefusesATemperatureOtherThanOne(string effort)
        => Assert.Single(Evaluate(Document(effort, "temperature: 0.0")));

    [Fact]
    public void TheOneTemperatureAReasoningEntryAccepts_Passes()
        => Assert.Empty(Evaluate(Document("high", "temperature: 1.0")));

    [Fact]
    public void AReasoningEntryWithNoTemperatureAtAll_Passes()
        => Assert.Empty(Evaluate(Document("high", null)));

    [Fact]
    public void AnEntryThatDoesNotReason_LeavesTemperatureFree()
        => Assert.Empty(Evaluate(Document("none", "temperature: 0.2")));

    [Fact]
    public void AnEntryThatNamesNoEffortAtAll_LeavesTemperatureFree()
        => Assert.Empty(Evaluate(Document(null, "temperature: 0.2")));

    [Fact]
    public void TheEffortOfAnotherEntry_DoesNotReachThisReference()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: two-entries
            agents:
              items:
                - { id: greeter, model: { ref: fill, temperature: 0.2 } }
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              llm:
                - { kind: openai, model: gpt-5.6-luna, as: reply, reasoningEffort: high }
                - { kind: openai, model: gpt-4.1-mini, as: fill }
            """;

        Assert.Empty(Evaluate(document));
    }

    [Fact]
    public void ATitlerAndAToolAndTheAgentDefaults_AreCheckedToo()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: every-holder
            tools:
              - { id: draw, kind: builtin, uses: ui.draw, model: { ref: reply, temperature: 0.2 } }
            agents:
              defaults:
                model: { ref: reply, temperature: 0.2 }
              items:
                - { id: greeter }
            titler:
              model: { ref: reply, temperature: 0.2 }
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              llm:
                - { kind: openai, model: gpt-5.6-luna, as: reply, reasoningEffort: low }
            """;

        Assert.Equal(
            ["/agents/defaults/model/temperature", "/titler/model/temperature", "/tools/0/model/temperature"],
            Evaluate(document).Select(static error => error.Pointer).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Runs the whole validator and keeps the errors of check 9 only.</summary>
    /// <param name="yaml">The document.</param>
    /// <returns>The errors this check raised.</returns>
    private static IReadOnlyList<ConfigurationError> Evaluate(string yaml)
    {
        var result = ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(yaml));

        Assert.All(result.Errors, error => Assert.Equal(ConfigurationCheck.ValueRange, error.Check));
        return result.Errors;
    }

    /// <summary>Writes one agent that points at one provider entry.</summary>
    /// <param name="effort">The entry's <c>reasoningEffort</c>, or <see langword="null"/> to omit it.</param>
    /// <param name="temperature">The reference's <c>temperature:</c> line, or <see langword="null"/> to omit it.</param>
    /// <returns>The YAML text.</returns>
    private static string Document(string? effort, string? temperature)
        => $$"""
            apiVersion: agentcore/v1
            name: reasoning
            agents:
              items:
                - id: greeter
                  model:
                    ref: reply
                    {{temperature ?? ""}}
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              llm:
                - kind: openai
                  model: gpt-5.6-luna
                  as: reply
                  {{(effort is null ? "" : $"reasoningEffort: {effort}")}}
            """;
}

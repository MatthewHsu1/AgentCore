using AgentCore.Application.Configuration.Parsing;
using Xunit;

namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// The guarantees the hand-written binder made, kept after the reader took its place.
/// </summary>
/// <remarks>
/// The binder now calls <c>JsonSerializer</c>, so every shape rule it used to enforce itself has to
/// be enforced somewhere else: in the document schema of check 1, or in one of the three converters.
/// Each test here pins one of those, so a schema edit that drops a rule fails rather than quietly
/// widening what a document may say.
/// </remarks>
public sealed class ConfigurationBinderTests
{
    private const string Header = "apiVersion: agentcore/v1\nname: pinned\n";

    private const string Providers = """
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
        """;

    /// <summary>Check 5 walks the members of an enumeration, and a member with no value is not a point.</summary>
    [Fact]
    public void AnEnumMemberWithNoValue_FailsWithThePointerOfThatMember()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml(
                Header + "state:\n  colour: { type: string, writer: const, value: red, enum: [red, null] }\n"));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer == "/state/colour/enum/1");
    }

    /// <summary>The frame size becomes a buffer length, so a number no buffer can hold is a document defect.</summary>
    [Fact]
    public void AMaxFrameSizeLargerThanABufferLength_FailsWithThePointerOfTheField()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml(
                Header + "providers:\n  call:   { kind: telnyx-relay, maxFrameBytes: 4294967296 }\n"
                + "  speech:\n    stt: { kind: telnyx-relay }\n    tts: { kind: telnyx-relay }\n"));

        Assert.Contains(failure.Errors, error => error.Pointer == "/providers/call/maxFrameBytes");
    }

    /// <summary>The export interval becomes a period in milliseconds, and the same bound applies.</summary>
    [Fact]
    public void AnExportIntervalLargerThanAPeriod_FailsWithThePointerOfTheField()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml(
                Header + Providers + "\n  telemetry: { kind: grafana, exportIntervalMs: 4294967296 }\n"));

        Assert.Contains(failure.Errors, error => error.Pointer == "/providers/telemetry/exportIntervalMs");
    }

    /// <summary>A source name of nothing but spaces listens to nothing, so it is a document defect.</summary>
    [Theory]
    [InlineData("sources")]
    [InlineData("meters")]
    public void ATelemetryNameOfSpaces_FailsWithThePointerOfThatName(string key)
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml(
                Header + Providers + $"\n  telemetry: {{ kind: grafana, {key}: [\" \"] }}\n"));

        Assert.Contains(failure.Errors, error => error.Pointer == $"/providers/telemetry/{key}/0");
    }

    /// <summary>An empty list is not an absent one: it listens to AgentCore alone.</summary>
    [Fact]
    public void AnEmptyTelemetryNameList_StaysEmptyRatherThanTakingTheDefaults()
    {
        var configuration = ConfigurationLoader.LoadYaml(
            Header + Providers + "\n  telemetry: { kind: grafana, sources: [], meters: [] }\n");

        Assert.Empty(configuration.Providers!.Telemetry!.Sources);
        Assert.Empty(configuration.Providers.Telemetry.Meters);
    }

    /// <summary>An absent list takes the defaults, which is not the same as an empty one.</summary>
    [Fact]
    public void AnAbsentTelemetryNameList_TakesTheDefaults()
    {
        var configuration = ConfigurationLoader.LoadYaml(
            Header + Providers + "\n  telemetry: { kind: grafana }\n");

        var telemetry = configuration.Providers!.Telemetry!;
        Assert.Equal(AgentCore.Application.Configuration.Schema.TelemetryProviderConfiguration.DefaultSources, telemetry.Sources);
        Assert.Equal(AgentCore.Application.Configuration.Schema.TelemetryProviderConfiguration.DefaultMeters, telemetry.Meters);
    }

    /// <summary>T61's floor is a correction and not a refusal, and the reader still runs the setter that applies it.</summary>
    [Fact]
    public void AnExportIntervalBelowTheFloor_IsRaisedToIt()
    {
        var configuration = ConfigurationLoader.LoadYaml(
            Header + Providers + "\n  telemetry: { kind: grafana, exportIntervalMs: 1000 }\n");

        Assert.Equal(
            AgentCore.Application.Configuration.Schema.TelemetryProviderConfiguration.MinimumExportIntervalMilliseconds,
            configuration.Providers!.Telemetry!.ExportIntervalMilliseconds);
    }

    /// <summary>A <c>when:</c> of empty text names no guard, and check 2 could only report a guard called nothing.</summary>
    [Fact]
    public void AGuardReferenceWithNoText_FailsWithThePointerOfTheField()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml(
                Header + "agents:\n  items: [{ id: only }]\n"
                + "policy:\n  initial: start\n  stages: [{ id: start, to: [{ stage: start, when: \"\" }] }]\n"));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/policy/stages/0/to/0/when", error.Pointer);
        Assert.Contains("guard name", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A whole number key does not take a number written with a fractional part.</summary>
    /// <remarks>
    /// The two writings fail in two different places. JSON Schema calls <c>30.5</c> a number and not
    /// an integer, so check 1 rejects it. It calls <c>30.0</c> an integer, because the fraction is
    /// zero — so that one reaches the reader, which will not put it in an <c>int</c>. Both end as one
    /// load error carrying the pointer of the field, which is what the hand-written binder did.
    /// </remarks>
    [Theory]
    [InlineData("30.5")]
    [InlineData("30.0")]
    public void AWholeNumberKeyWrittenWithAFraction_FailsWithThePointerOfTheField(string written)
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadJson(
                $$"""
                {
                  "apiVersion": "agentcore/v1",
                  "name": "pinned",
                  "providers": {
                    "call": { "kind": "telnyx-relay", "idleTimeoutSeconds": {{written}} },
                    "speech": { "stt": { "kind": "telnyx-relay" }, "tts": { "kind": "telnyx-relay" } }
                  }
                }
                """));

        Assert.Contains(failure.Errors, error => error.Pointer == "/providers/call/idleTimeoutSeconds");
        Assert.DoesNotContain("System.", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The reader reports a path, and section 8.7 asks for a JSON Pointer.</summary>
    [Theory]
    [InlineData(null, "")]
    [InlineData("$", "")]
    [InlineData("$.name", "/name")]
    [InlineData("$.tools[0].request.url", "/tools/0/request/url")]
    [InlineData("$['a/b'].c", "/a~1b/c")]
    [InlineData("$['a~b']", "/a~0b")]
    public void ThePathTheReaderReports_BecomesAJsonPointer(string? path, string expected)
        => Assert.Equal(expected, ConfigurationBinder.Pointer(path));

    /// <summary>The reader names CLR types, and whoever wrote the document has never heard of them.</summary>
    [Theory]
    [InlineData("The JSON value could not be converted to System.String. Path: $.name | LineNumber: 0 | BytePositionInLine: 9.", "a string is expected")]
    [InlineData("The JSON value could not be converted to System.Int32.", "a whole number is expected")]
    [InlineData("The JSON value could not be converted to System.Nullable`1[System.Int32].", "a whole number is expected")]
    [InlineData("The JSON value could not be converted to System.Double.", "a number is expected")]
    [InlineData("The JSON value could not be converted to System.Boolean.", "a boolean is expected")]
    [InlineData("JSON deserialization for type 'X' was missing required properties, including: 'name'.", "the required property 'name' is missing")]
    [InlineData("something new the reader learned to say", "something new the reader learned to say")]
    public void TheMessageTheReaderWrites_IsRewrittenForTheAuthor(string written, string expected)
        => Assert.Equal(expected, ConfigurationBinder.Explain(written));
}

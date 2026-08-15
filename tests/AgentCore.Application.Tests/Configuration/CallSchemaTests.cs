using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Xunit;

namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// <c>providers.call</c> names the vendor that carries the call, and the limits of its socket.
/// </summary>
/// <remarks>
/// The media plane is not <c>providers.speech</c>, which is recognition and synthesis, and it is not
/// <c>providers.telephony</c>, which is call control. Section 8.2 asks a document that writes a
/// <c>providers:</c> section to name both the pipe and the ears, so the two are required together.
/// </remarks>
public sealed class CallSchemaTests
{
    [Fact]
    public void TheCallBlockBindsItsKindAndItsThreeKnobs()
    {
        var configuration = Load("""
            providers:
              call:
                kind: telnyx-relay
                idleTimeoutSeconds: 30
                closeTimeoutSeconds: 5
                maxFrameBytes: 65536
              speech: { kind: telnyx-relay }
            """);

        var call = configuration.Providers!.Call!;
        Assert.Equal("telnyx-relay", call.Kind);
        Assert.Equal(30, call.IdleTimeoutSeconds);
        Assert.Equal(5, call.CloseTimeoutSeconds);
        Assert.Equal(65536, call.MaxFrameBytes);
    }

    [Fact]
    public void TheThreeKnobsAreOptional()
    {
        var configuration = Load("""
            providers:
              call:   { kind: telnyx-relay }
              speech: { kind: telnyx-relay }
            """);

        var call = configuration.Providers!.Call!;
        Assert.Null(call.IdleTimeoutSeconds);
        Assert.Null(call.CloseTimeoutSeconds);
        Assert.Null(call.MaxFrameBytes);
    }

    [Fact]
    public void AMissingCallBlockFailsTheLoad()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => Load("""
                providers:
                  speech: { kind: telnyx-relay }
                """));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains("call", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingSpeechBlockFailsTheLoad()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => Load("""
                providers:
                  call: { kind: telnyx-relay }
                """));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains("speech", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownFieldUnderCallFailsTheLoad()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => Load("""
                providers:
                  call:   { kind: telnyx-relay, idleTimeoutSecnods: 30 }
                  speech: { kind: telnyx-relay }
                """));

        // additionalProperties names the offending property in the pointer rather than the text, and
        // ConfigurationLoadException.Message carries pointer and message together for every error.
        Assert.Contains("idleTimeoutSecnods", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InfiniteIsSpelledMinusOne()
    {
        var configuration = Load("""
            providers:
              call:   { kind: telnyx-relay, idleTimeoutSeconds: -1 }
              speech: { kind: telnyx-relay }
            """);

        Assert.Equal(-1, configuration.Providers!.Call!.IdleTimeoutSeconds);
    }

    /// <summary>Loads one <c>providers:</c> section under the smallest complete document header.</summary>
    /// <param name="providers">The <c>providers:</c> section, written at the document's own margin.</param>
    /// <returns>The loaded document.</returns>
    /// <remarks>
    /// Check 1 of section 8.5 is the only check <c>ConfigurationLoader.LoadYaml</c> runs, and the
    /// document root requires <c>apiVersion</c> and <c>name</c> and nothing else. Every other test
    /// in this folder loads the same way.
    /// </remarks>
    private static AgentCoreConfiguration Load(string providers)
        => ConfigurationLoader.LoadYaml(
            "apiVersion: agentcore/v1\nname: call-schema\n" + providers);
}

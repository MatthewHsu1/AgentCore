using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Ports;
using AgentCore.Application.Providers;
using Xunit;

namespace AgentCore.Application.Tests.Providers;

/// <summary>
/// The one selection every vendor seam shares: a <c>kind</c> in the document, and the one adapter
/// the host registered for it.
/// </summary>
/// <remarks>
/// This replaces <c>SpeechAdapterSelectorTests</c>. The seam it is proven through is still speech,
/// because that is the seam whose wording the shared selector was lifted from, but nothing here is
/// speech-specific: the adapter is an <see cref="IVendorAdapter"/> and the seam is a parameter.
/// </remarks>
public sealed class VendorAdapterSelectorTests
{
    private static readonly VendorSeam SpeechSeam =
        new("providers.speech", "/providers/speech/kind", "options.UseSpeech(...)");

    private sealed class FakeAdapter(string kind) : IVendorAdapter
    {
        public string Kind { get; } = kind;
    }

    [Fact]
    public void TheNamedKindIsSelected()
    {
        var relay = new FakeAdapter("telnyx-relay");

        var selected = VendorAdapterSelector.Select(
            "telnyx-relay", [new FakeAdapter("deepgram"), relay], SpeechSeam);

        Assert.Same(relay, selected);
    }

    [Fact]
    public void TheKindMatchesWithoutRegardToCase()
    {
        var relay = new FakeAdapter("telnyx-relay");

        var selected = VendorAdapterSelector.Select("TELNYX-RELAY", [relay], SpeechSeam);

        Assert.Same(relay, selected);
    }

    [Fact]
    public void AKindNoAdapterServesFailsTheStart()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => VendorAdapterSelector.Select(
                "deepgram", [new FakeAdapter("telnyx-relay")], SpeechSeam));

        Assert.Contains("deepgram", failure.Message, StringComparison.Ordinal);
        Assert.Contains("'telnyx-relay'", failure.Message, StringComparison.Ordinal);
        Assert.Equal("/providers/speech/kind", failure.Errors[0].Pointer);
        Assert.Equal(ConfigurationCheck.ReferenceResolution, failure.Errors[0].Check);
    }

    [Fact]
    public void AHostThatRegisteredNothingIsToldWhichCallToMake()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            // The type argument is written out because an empty list infers no adapter type.
            () => VendorAdapterSelector.Select<IVendorAdapter>("deepgram", [], SpeechSeam));

        Assert.Contains("no adapter", failure.Message, StringComparison.Ordinal);
        Assert.Contains("options.UseSpeech(...)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoAdaptersForOneKindFailTheStart()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => VendorAdapterSelector.Select(
                "telnyx-relay",
                [new FakeAdapter("telnyx-relay"), new FakeAdapter("telnyx-relay")],
                SpeechSeam));

        Assert.Contains("two adapters", failure.Message, StringComparison.Ordinal);
        Assert.Equal("/providers/speech/kind", failure.Errors[0].Pointer);
    }

    [Fact]
    public void TheSeamNamesItselfInEveryMessage()
    {
        var telemetrySeam = new VendorSeam(
            "providers.telemetry", "/providers/telemetry/kind", "options.UseTelemetry(...)");

        var failure = Assert.Throws<ConfigurationLoadException>(
            () => VendorAdapterSelector.Select<IVendorAdapter>("grafana", [], telemetrySeam));

        Assert.Contains("providers.telemetry", failure.Message, StringComparison.Ordinal);
        Assert.Contains("options.UseTelemetry(...)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullAdaptersAreRefused()
    {
        Assert.Throws<ArgumentNullException>(
            () => VendorAdapterSelector.Select<IVendorAdapter>("openai", null!, SpeechSeam));
    }

    [Fact]
    public void EachSeamKeepsItsOwnPluralNoun()
    {
        // Four seams wrote "stores", "collectors", "endpoints", and "vendors" before they shared one
        // selector. The plural noun is a seam's own word, so it travels with the seam.
        var knowledgeSeam = new VendorSeam(
            "providers.knowledge.search", "/providers/knowledge/search", "options.UseKnowledge(...)", "stores");

        var failure = Assert.Throws<ConfigurationLoadException>(
            () => VendorAdapterSelector.Select(
                "filesystem",
                [new FakeAdapter("filesystem"), new FakeAdapter("filesystem")],
                knowledgeSeam));

        Assert.Contains("names two stores", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASeamThatNamesNoPluralSaysVendors()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => VendorAdapterSelector.Select(
                "telnyx-relay",
                [new FakeAdapter("telnyx-relay"), new FakeAdapter("telnyx-relay")],
                SpeechSeam));

        // The three-argument constructor is what keeps the speech wording, and it is the default
        // every seam that never had a noun of its own gets.
        Assert.Contains("names two vendors", failure.Message, StringComparison.Ordinal);
    }
}

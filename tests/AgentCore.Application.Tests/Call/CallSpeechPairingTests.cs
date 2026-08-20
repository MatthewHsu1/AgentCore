using AgentCore.Application.Call;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Xunit;

namespace AgentCore.Application.Tests.Call;

public sealed class CallSpeechPairingTests
{
    private sealed class FakeCallAdapter(string kind, bool carriesText) : ICallAdapter
    {
        public string Kind { get; } = kind;

        public bool CarriesText { get; } = carriesText;
    }

    private static CallProviderConfiguration Call(string kind) => new() { Kind = kind };

    private static SpeechProviderConfiguration Speech(string stt, string tts) => new()
    {
        Stt = new VendorProviderConfiguration { Kind = stt },
        Tts = new VendorProviderConfiguration { Kind = tts },
    };

    [Fact]
    public void ATextCarryingTransportBesideItsOwnSpeechKindPasses()
    {
        CallSpeechPairing.Validate(
            Call("telnyx-relay"),
            Speech("telnyx-relay", "telnyx-relay"),
            new FakeCallAdapter("telnyx-relay", carriesText: true));
    }

    [Fact]
    public void TheKindsMatchWithoutRegardToCase()
    {
        CallSpeechPairing.Validate(
            Call("telnyx-relay"),
            Speech("TELNYX-RELAY", "Telnyx-Relay"),
            new FakeCallAdapter("telnyx-relay", carriesText: true));
    }

    [Fact]
    public void ATextCarryingTransportBesideAnotherRecognitionKindFailsTheStart()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => CallSpeechPairing.Validate(
                Call("telnyx-relay"),
                Speech("deepgram", "telnyx-relay"),
                new FakeCallAdapter("telnyx-relay", carriesText: true)));

        Assert.Single(failure.Errors);
        Assert.Equal("/providers/speech/stt/kind", failure.Errors[0].Pointer);
        Assert.Equal(ConfigurationCheck.ReferenceResolution, failure.Errors[0].Check);
        Assert.Contains("telnyx-relay", failure.Message, StringComparison.Ordinal);
        Assert.Contains("deepgram", failure.Message, StringComparison.Ordinal);
        Assert.Contains("providers.speech.stt", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATextCarryingTransportBesideAnotherSynthesisKindFailsTheStart()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => CallSpeechPairing.Validate(
                Call("telnyx-relay"),
                Speech("telnyx-relay", "elevenlabs"),
                new FakeCallAdapter("telnyx-relay", carriesText: true)));

        Assert.Single(failure.Errors);
        Assert.Equal("/providers/speech/tts/kind", failure.Errors[0].Pointer);
        Assert.Equal(ConfigurationCheck.ReferenceResolution, failure.Errors[0].Check);
        Assert.Contains("telnyx-relay", failure.Message, StringComparison.Ordinal);
        Assert.Contains("elevenlabs", failure.Message, StringComparison.Ordinal);
        Assert.Contains("providers.speech.tts", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BothRolesMismatchedReportTwoErrors()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => CallSpeechPairing.Validate(
                Call("telnyx-relay"),
                Speech("deepgram", "elevenlabs"),
                new FakeCallAdapter("telnyx-relay", carriesText: true)));

        Assert.Equal(2, failure.Errors.Count);
        Assert.Equal("/providers/speech/stt/kind", failure.Errors[0].Pointer);
        Assert.Equal("/providers/speech/tts/kind", failure.Errors[1].Pointer);
    }

    [Fact]
    public void AnAudioCarryingTransportBesideTwoOtherSpeechKindsPasses()
    {
        // This is the split shape: a SIP leg carries audio, one vendor turns it into text, and
        // another turns text back into what the caller hears.
        CallSpeechPairing.Validate(
            Call("sip"),
            Speech("deepgram", "elevenlabs"),
            new FakeCallAdapter("sip", carriesText: false));
    }

    [Fact]
    public void NullArgumentsAreRefused()
    {
        var adapter = new FakeCallAdapter("telnyx-relay", carriesText: true);
        var speech = Speech("telnyx-relay", "telnyx-relay");

        Assert.Throws<ArgumentNullException>(
            () => CallSpeechPairing.Validate(null!, speech, adapter));
        Assert.Throws<ArgumentNullException>(
            () => CallSpeechPairing.Validate(Call("telnyx-relay"), null!, adapter));
        Assert.Throws<ArgumentNullException>(
            () => CallSpeechPairing.Validate(Call("telnyx-relay"), speech, null!));
    }
}

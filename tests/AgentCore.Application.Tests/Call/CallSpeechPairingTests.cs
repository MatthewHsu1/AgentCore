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

    private static VendorProviderConfiguration Speech(string kind) => new() { Kind = kind };

    [Fact]
    public void ATextCarryingTransportBesideItsOwnSpeechKindPasses()
    {
        CallSpeechPairing.Validate(
            Call("telnyx-relay"),
            Speech("telnyx-relay"),
            new FakeCallAdapter("telnyx-relay", carriesText: true));
    }

    [Fact]
    public void TheKindsMatchWithoutRegardToCase()
    {
        CallSpeechPairing.Validate(
            Call("telnyx-relay"),
            Speech("TELNYX-RELAY"),
            new FakeCallAdapter("telnyx-relay", carriesText: true));
    }

    [Fact]
    public void ATextCarryingTransportBesideAnotherSpeechKindFailsTheStart()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => CallSpeechPairing.Validate(
                Call("telnyx-relay"),
                Speech("deepgram"),
                new FakeCallAdapter("telnyx-relay", carriesText: true)));

        Assert.Equal("/providers/speech/kind", failure.Errors[0].Pointer);
        Assert.Contains("telnyx-relay", failure.Message, StringComparison.Ordinal);
        Assert.Contains("deepgram", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAudioCarryingTransportBesideAnotherSpeechKindPasses()
    {
        // This is the split shape: a SIP leg carries audio, and Deepgram turns it into text.
        CallSpeechPairing.Validate(
            Call("sip"),
            Speech("deepgram"),
            new FakeCallAdapter("sip", carriesText: false));
    }

    [Fact]
    public void NullArgumentsAreRefused()
    {
        var adapter = new FakeCallAdapter("telnyx-relay", carriesText: true);

        Assert.Throws<ArgumentNullException>(
            () => CallSpeechPairing.Validate(null!, Speech("telnyx-relay"), adapter));
        Assert.Throws<ArgumentNullException>(
            () => CallSpeechPairing.Validate(Call("telnyx-relay"), null!, adapter));
        Assert.Throws<ArgumentNullException>(
            () => CallSpeechPairing.Validate(Call("telnyx-relay"), Speech("telnyx-relay"), null!));
    }
}

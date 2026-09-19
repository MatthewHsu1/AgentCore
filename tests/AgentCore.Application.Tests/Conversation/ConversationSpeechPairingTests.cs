using AgentCore.Application.Conversation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Xunit;

namespace AgentCore.Application.Tests.Conversation;

public sealed class ConversationSpeechPairingTests
{
    private sealed class FakeConversationAdapter(string kind, bool carriesText) : IConversationAdapter
    {
        public string Kind { get; } = kind;

        public bool CarriesText { get; } = carriesText;
    }

    private static ConversationProviderConfiguration Conversation(string kind) => new() { Kind = kind };

    private static SpeechProviderConfiguration Speech(string stt, string tts) => new()
    {
        Stt = new VendorProviderConfiguration { Kind = stt },
        Tts = new VendorProviderConfiguration { Kind = tts },
    };

    [Fact]
    public void ATextCarryingTransportBesideItsOwnSpeechKindPasses()
    {
        ConversationSpeechPairing.Validate(
            Conversation("telnyx-relay"),
            Speech("telnyx-relay", "telnyx-relay"),
            new FakeConversationAdapter("telnyx-relay", carriesText: true));
    }

    [Fact]
    public void TheKindsMatchWithoutRegardToCase()
    {
        ConversationSpeechPairing.Validate(
            Conversation("telnyx-relay"),
            Speech("TELNYX-RELAY", "Telnyx-Relay"),
            new FakeConversationAdapter("telnyx-relay", carriesText: true));
    }

    [Fact]
    public void ATextCarryingTransportBesideAnotherRecognitionKindFailsTheStart()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConversationSpeechPairing.Validate(
                Conversation("telnyx-relay"),
                Speech("deepgram", "telnyx-relay"),
                new FakeConversationAdapter("telnyx-relay", carriesText: true)));

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
            () => ConversationSpeechPairing.Validate(
                Conversation("telnyx-relay"),
                Speech("telnyx-relay", "elevenlabs"),
                new FakeConversationAdapter("telnyx-relay", carriesText: true)));

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
            () => ConversationSpeechPairing.Validate(
                Conversation("telnyx-relay"),
                Speech("deepgram", "elevenlabs"),
                new FakeConversationAdapter("telnyx-relay", carriesText: true)));

        Assert.Equal(2, failure.Errors.Count);
        Assert.Equal("/providers/speech/stt/kind", failure.Errors[0].Pointer);
        Assert.Equal("/providers/speech/tts/kind", failure.Errors[1].Pointer);
    }

    [Fact]
    public void AnAudioCarryingTransportBesideTwoOtherSpeechKindsPasses()
    {
        // This is the split shape: a SIP leg carries audio, one vendor turns it into text, and
        // another turns text back into what the caller hears.
        ConversationSpeechPairing.Validate(
            Conversation("sip"),
            Speech("deepgram", "elevenlabs"),
            new FakeConversationAdapter("sip", carriesText: false));
    }

    [Fact]
    public void NullArgumentsAreRefused()
    {
        var adapter = new FakeConversationAdapter("telnyx-relay", carriesText: true);
        var speech = Speech("telnyx-relay", "telnyx-relay");

        Assert.Throws<ArgumentNullException>(
            () => ConversationSpeechPairing.Validate(null!, speech, adapter));
        Assert.Throws<ArgumentNullException>(
            () => ConversationSpeechPairing.Validate(Conversation("telnyx-relay"), null!, adapter));
        Assert.Throws<ArgumentNullException>(
            () => ConversationSpeechPairing.Validate(Conversation("telnyx-relay"), speech, null!));
    }
}

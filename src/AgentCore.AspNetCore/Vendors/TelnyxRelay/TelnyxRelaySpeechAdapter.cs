using AgentCore.Application.Ports;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay;

/// <summary>
/// The selection face of the one speech vendor this solution bundles.
/// </summary>
/// <remarks>
/// <para>
/// D28 buys the whole speech layer — speech to text, turn detection, text to speech, and
/// interruption — inside Telnyx Conversation Relay, so this vendor is a transport rather than a
/// service the process dials. A host registers this through
/// <c>AgentCoreOptions.UseSpeech</c>, and a document that writes
/// <c>providers.speech: { kind: telnyx-relay }</c> then selects it. The socket itself belongs to
/// <c>providers.call</c>, which <c>app.MapCall()</c> reads — and because this vendor bundles both,
/// the two blocks must name it together or the start fails.
/// </para>
/// <para>
/// It carries a <c>kind</c> and nothing else, because <see cref="ISpeechAdapter"/> does. A relay call
/// is born from a socket the host already accepted, so there is nothing here to create at selection
/// time: the frames arriving on that socket are the channel.
/// </para>
/// </remarks>
public sealed class TelnyxRelaySpeechAdapter : ISpeechAdapter
{
    /// <summary>The one <c>providers.speech.kind</c> value this vendor answers to.</summary>
    public const string TelnyxRelayKind = "telnyx-relay";

    /// <inheritdoc/>
    public string Kind => TelnyxRelayKind;
}

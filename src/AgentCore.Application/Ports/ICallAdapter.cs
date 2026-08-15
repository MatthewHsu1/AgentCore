namespace AgentCore.Application.Ports;

/// <summary>
/// Names the vendor behind one <c>providers.call</c> value: who carries the call.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>selection</b> face, and it is all the core can see. The <b>routing</b> face
/// lives in <c>AgentCore.AspNetCore</c> as <c>ICallTransportAdapter</c>, because it names
/// <c>IEndpointRouteBuilder</c> and this assembly references no ASP.NET Core package.
/// </para>
/// <para>
/// A vendor this process dials <b>out</b> to implements this and stops. It has no inbound URL, so
/// it is never forced to grow a routing member it would have to throw from.
/// </para>
/// </remarks>
public interface ICallAdapter : IVendorAdapter
{
    /// <summary>
    /// Gets whether this transport's frames already carry text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="true"/> means the vendor performs recognition and synthesis itself — the
    /// Telnyx Conversation Relay does — so <b>both</b> speech roles,
    /// <c>providers.speech.stt.kind</c> and <c>providers.speech.tts.kind</c>, must name that same
    /// vendor, and <see cref="Call.CallSpeechPairing"/> enforces it.
    /// </para>
    /// <para>
    /// <see langword="false"/> means the frames carry audio and a separate speech vendor turns it
    /// into text. That adapter runs recognition and synthesis inside itself: audio never leaves it,
    /// so no audio type enters this assembly either way.
    /// </para>
    /// <para>
    /// <b>This is declared here rather than written in the document.</b> A <c>payload: text</c>
    /// field beside <c>kind</c> could only agree with the vendor or lie about it, and an adapter
    /// cannot be wrong about its own wire.
    /// </para>
    /// </remarks>
    bool CarriesText { get; }
}

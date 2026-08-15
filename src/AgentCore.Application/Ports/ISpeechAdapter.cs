namespace AgentCore.Application.Ports;

/// <summary>
/// Names the vendor behind one speech role's <c>kind</c>, <c>stt</c> or <c>tts</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the speech mirror of <see cref="IChatClientAdapter"/>, <see cref="IKnowledgeStoreAdapter"/>,
/// <see cref="IModerationAdapter"/>, and <see cref="ITelemetryAdapter"/>, and it takes the same shape:
/// the host lists the vendors it supports once, <c>providers.speech</c> names which one the
/// document means for each role — <c>stt</c> is recognition and <c>tts</c> is synthesis — and a
/// document that changes vendors changes no code.
/// </para>
/// <para>
/// <b>It declares no member of its own, so it carries a <see cref="IVendorAdapter.Kind"/> and
/// nothing else, on purpose.</b> D15 makes every public member
/// a permanent obligation, so a member is added when a caller exists for it and not before. The only
/// vendor this solution ships is a bundled transport — the Telnyx Conversation Relay — and a relay
/// call is born from a socket the host already accepted: there is nothing for the adapter to create at
/// selection time, because the frames arriving on that socket are the channel. A create method here
/// today would be a signature no caller passes through and every future vendor must still honour. The
/// first split vendor, whose speech is a separate service the process must dial, is the caller that
/// earns the creation surface, and it adds it then.
/// </para>
/// <para>
/// So each role's <c>kind</c> is a <b>naming</b> field, not a selecting one: it answers "which
/// vendor did the document mean", and nothing here turns that answer into a constructed adapter.
/// <see cref="Call.CallSpeechPairing"/> is what reads the names today, and only to check them
/// against <c>providers.call.kind</c> — because a transport whose frames already carry text, such as
/// the relay this solution ships, is itself the recognizer and the synthesizer, so once the names
/// agree there is nothing left to build. <see cref="ICallChannelFactory"/> stays the seam's
/// <b>per-call</b> face: it answers "open both halves of this one call", opened once for every call
/// rather than once while the host starts.
/// </para>
/// <para>
/// <b><c>providers.call</c> and <c>providers.speech</c> are required of one another.</b> The schema
/// writes <c>required: ["call", "speech"]</c> on the <c>providers</c> object, so a document that
/// writes a <c>providers</c> section at all writes both, and <c>AddAgentCoreAsync</c> refuses a
/// configuration that registered a call transport and named no speech vendor. A transport whose
/// frames already carry text must further be named by <b>both</b> speech roles, because such a
/// vendor performs recognition and synthesis itself and a document naming it for the pipe and
/// something else for the ears or the mouth describes a deployment that cannot exist.
/// <see cref="Call.CallSpeechPairing"/> is what enforces that, while the host starts and before any
/// route is mapped, because the agreement is a fact about the document rather than about a route.
/// </para>
/// </remarks>
public interface ISpeechAdapter : IVendorAdapter
{
}

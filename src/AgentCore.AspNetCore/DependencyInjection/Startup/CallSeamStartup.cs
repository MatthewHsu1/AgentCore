using AgentCore.Application.Call;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Providers;
using AgentCore.AspNetCore.Call;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>The two provider blocks a call arrives on: <c>providers.call</c> and <c>providers.speech</c>.</summary>
/// <remarks>
/// <para>
/// <b>A transport is owned by the <c>Map*</c> extension that maps it, and that is still true.</b>
/// D28 buys the whole speech layer inside Telnyx Conversation Relay, so <c>providers.speech</c>
/// names the relay: the code that <i>selects</i> it is
/// <c>AgentCore.AspNetCore/Vendors/TelnyxRelay/</c>, an inbound <c>Map*</c> extension that turns relay
/// frames into <see cref="IConversationPort"/> calls, and that extension is what asks
/// <see cref="VendorAdapterSelector"/> — the one selector every vendor seam shares — whether this
/// document named its vendor. <see cref="AgentCoreOptions.UseSpeech"/> hands the registered vendors
/// over, and this class only puts that list in the container for the <c>Map*</c> extension to
/// resolve. <c>providers.telephony</c> names the vendor behind <c>ITelephonyControlPort</c> —
/// answer, start, conference transfer, hang up — whose adapter is
/// <c>AgentCore.Infrastructure/Telephony/Telnyx/</c>, and that port is not declared yet.
/// Item 6c also forbids any audio in this solution, and neither adapter will hold one.
/// </para>
/// <para>
/// <b>That reasoning held while speech was one optional block, and it does not hold for a
/// consistency rule between two required blocks.</b> <c>providers.call</c> and
/// <c>providers.speech</c> are now required of one another, and whether they agree is a fact about
/// the document rather than about any route: a transport whose frames already carry text performs
/// recognition and synthesis itself, so a document that names it for the pipe and something else
/// for the ears or the mouth describes a deployment that cannot exist. Each role is checked on its
/// own, so a document that gets both wrong hears about both. That is true or false before anything is
/// mapped. So when <see cref="AgentCoreOptions.UseCall"/> registered a transport, this class selects
/// the one <c>providers.call.kind</c> names and runs <see cref="CallSpeechPairing"/> over the pair —
/// precisely so a host that forgets <c>app.MapCall()</c> still learns its document contradicts
/// itself, rather than answering a call that connects and never transcribes. It selects no speech
/// vendor while doing it; the <c>Map*</c> extension still owns that.
/// </para>
/// </remarks>
internal static class CallSeamStartup
{
    /// <summary>Puts the registered transports in the container, and checks the two blocks agree.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The loaded document. It carries both provider blocks.</param>
    /// <param name="options">The options the host filled. It carries the registered vendors.</param>
    /// <exception cref="ConfigurationLoadException">
    /// The host registered a call transport and the document names no <c>providers.call</c> or no
    /// <c>providers.speech</c> block, or the two blocks contradict one another.
    /// </exception>
    internal static void Register(
        IServiceCollection services,
        AgentCoreConfiguration configuration,
        AgentCoreOptions options)
    {
        if (options.Speech is { } speechAdapters)
        {
            // Registered here, and selected by nothing. providers.speech names the recognition
            // vendor and the synthesis vendor, but no code in this solution turns either name into a
            // constructed adapter: the one transport shipped today carries text, so it is itself both
            // and there is nothing to build. The list goes in the container beside the document so
            // that a vendor which does need constructing has somewhere to be found.
            //
            // The call seam below reads both providers.speech roles all the same, and that is not a
            // contradiction: it never selects a speech vendor, it only checks that the blocks
            // agree — see this type's own remarks.
            services.AddSingleton<IReadOnlyList<ISpeechAdapter>>(speechAdapters);
        }

        if (options.Call is { } callAdapters)
        {
            // Registering a vendor is what turns this seam on. A host that registered none is not
            // asked what its document says, exactly as telemetry, knowledge, and moderation are not
            // read when a host registered no adapter for them.
            var callEntry = configuration.Providers?.Call
                ?? throw MissingCallBlock();

            var selectedCall = VendorAdapterSelector.Select(
                callEntry.Kind, callAdapters, CallSeams.Call);

            var speechEntry = configuration.Providers?.Speech
                ?? throw MissingSpeechBlock();

            // Read here, and not only where the route is mapped. Agreement between two document
            // entries is a document fact: it is true or false whether or not anything is routed, so
            // a host that forgets app.MapCall() still learns its document contradicts itself.
            CallSpeechPairing.Validate(callEntry, speechEntry, selectedCall);

            services.AddSingleton<IReadOnlyList<ICallAdapter>>(callAdapters);
        }
    }

    /// <summary>Refuses a configuration that turned the call seam on and named no transport.</summary>
    /// <returns>The failure to throw, pointed at the block that is missing.</returns>
    /// <remarks>
    /// <para>
    /// <c>required: ["call", "speech"]</c> sits on the <c>providers</c> <b>object</b>, and the root
    /// of the schema requires only <c>apiVersion</c> and <c>name</c> — <c>providers</c> itself is
    /// optional. So two routes reach here legitimately: a host that built an
    /// <see cref="AgentCoreConfiguration"/> in code, which passes through no schema at all, and a
    /// valid loaded document that writes no <c>providers</c> section whatsoever.
    /// </para>
    /// <para>
    /// Neither is a schema defect the loader could have caught, so neither may arrive as a
    /// <see cref="NullReferenceException"/> out of the guard below it.
    /// </para>
    /// </remarks>
    private static ConfigurationLoadException MissingCallBlock()
        => new(new ConfigurationError
        {
            Pointer = "/providers/call",
            Message =
                "This host registered a call transport with options.UseCall(...), and this "
                + "configuration writes no providers.call block for a kind to be picked from. A "
                + "document that writes a providers section names both call and speech, because the "
                + "schema requires them there; a document that writes no providers section at all is "
                + "valid, and so is a configuration a host built in code, which passes through no "
                + "schema. Write providers.call: { kind: ... }, or drop the options.UseCall(...) call.",
            Check = ConfigurationCheck.ReferenceResolution,
        });

    /// <summary>Refuses a configuration whose call transport has no speech block to be paired with.</summary>
    /// <returns>The failure to throw, pointed at the block that is missing.</returns>
    /// <remarks>
    /// Reachable by the same two routes as <see cref="MissingCallBlock"/>, and for the same reason:
    /// the schema requires the two blocks of one another and requires the <c>providers</c> object of
    /// nobody. A configuration that named a call transport and no speech vendor cannot be paired, so
    /// it is refused here rather than passed to the guard as a <see langword="null"/>.
    /// </remarks>
    private static ConfigurationLoadException MissingSpeechBlock()
        => new(new ConfigurationError
        {
            Pointer = "/providers/speech",
            Message =
                "This configuration names providers.call and no providers.speech, so there is "
                + "nothing to check the transport against: a transport that carries text is itself "
                + "the recognizer, and one that carries audio needs a speech vendor named. A "
                + "document that writes a providers section names both, because the schema requires "
                + "them there; a document that writes no providers section at all is valid, and so "
                + "is a configuration a host built in code, which passes through no schema. Write "
                + "providers.speech with both of its roles: stt: { kind: ... } and tts: { kind: ... }.",
            Check = ConfigurationCheck.ReferenceResolution,
        });
}

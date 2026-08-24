using AgentCore.Application.Call;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Providers;
using AgentCore.AspNetCore.Call;
using Microsoft.AspNetCore.Http;

namespace AgentCore.AspNetCore.DependencyInjection.Startup;

/// <summary>What the call seam produced while the host started.</summary>
/// <param name="Call">The transports <see cref="AgentCoreOptions.UseCall"/> registered.</param>
/// <param name="Speech">
/// The vendors <see cref="AgentCoreOptions.UseSpeech"/> registered, selected by nothing.
/// <c>providers.speech</c> names the recognition vendor and the synthesis vendor, but no code in
/// this solution turns either name into a constructed adapter: the one transport shipped today
/// carries text, so it is itself both and there is nothing to build. The list goes in the container
/// beside the document so that a vendor which does need constructing has somewhere to be found.
/// </param>
/// <param name="Handler">
/// What <c>MapCall</c>'s route runs, or <see langword="null"/> when this document routes no inbound
/// call — the host registered no transport, wrote no <c>providers.call</c> block, or named a vendor
/// this process dials out to, which has no inbound URL.
/// </param>
/// <param name="Unroutable">
/// Why <paramref name="Handler"/> is <see langword="null"/>, in the words a deployer can act on, or
/// <see langword="null"/> when a call does route.
/// </param>
internal readonly record struct CallSeamAdapters(
    IReadOnlyList<ICallAdapter>? Call,
    IReadOnlyList<ISpeechAdapter>? Speech,
    RequestDelegate? Handler,
    string? Unroutable);

/// <summary>The two provider blocks a call arrives on: <c>providers.call</c> and <c>providers.speech</c>.</summary>
internal static class CallSeamStartup
{
    /// <summary>Checks the two blocks agree, and hands back the vendor lists to register.</summary>
    /// <param name="configuration">The loaded document. It carries both provider blocks.</param>
    /// <param name="options">The options the host filled. It carries the registered vendors.</param>
    /// <returns>The two lists, each one or <see langword="null"/> when the host registered none.</returns>
    internal static CallSeamAdapters Build(
        AgentCoreConfiguration configuration,
        AgentCoreOptions options)
    {
        if (options.Call is not { } callAdapters)
        {
            return new CallSeamAdapters(
                null, options.Speech, null, "this host registered no call adapter");
        }

        // Registering a vendor is what turns this seam on. A host that registered none is not asked
        // what its document says, exactly as telemetry, knowledge, and moderation are not read when
        // a host registered no adapter for them.
        var callEntry = configuration.Providers?.Call
            ?? throw MissingCallBlock();

        var selectedCall = VendorAdapterSelector.Select(
            callEntry.Kind, callAdapters, CallSeams.Call);

        var speechEntry = configuration.Providers?.Speech
            ?? throw MissingSpeechBlock();

        // Read here, and not only where the route is mapped. Agreement between two document entries
        // is a document fact: it is true or false whether or not anything is routed, so a host that
        // forgets app.MapCall() still learns its document contradicts itself.
        CallSpeechPairing.Validate(callEntry, speechEntry, selectedCall);

        // Built here and not where the route is mapped, so an unusable limit in providers.call stops
        // the host rather than the first call that arrives on it. A vendor this process dials out to
        // has no inbound URL, and that is not a failure: it is the other half of the seam working.
        return selectedCall is ICallTransportAdapter transport
            ? new CallSeamAdapters(callAdapters, options.Speech, transport.CreateHandler(callEntry), null)
            : new CallSeamAdapters(
                callAdapters,
                options.Speech,
                null,
                $"'{selectedCall.Kind}' is a vendor this process dials out to, so it answers no inbound route");
    }

    /// <summary>Refuses a configuration that turned the call seam on and named no transport.</summary>
    /// <returns>The failure to throw, pointed at the block that is missing.</returns>
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

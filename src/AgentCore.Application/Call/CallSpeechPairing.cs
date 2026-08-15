using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Call;

/// <summary>
/// Refuses a document whose call transport and speech vendor cannot coexist.
/// </summary>
/// <remarks>
/// <para>
/// A transport whose frames already carry text <i>is</i> the recognizer and the synthesizer. A
/// document naming it for the pipe and something else for the ears describes a deployment that
/// cannot exist, and the failure it would otherwise produce — a call that connects and never
/// transcribes — is among the most expensive kinds to diagnose.
/// </para>
/// <para>
/// <b>No vendor name appears here.</b> <see cref="ICallAdapter.CarriesText"/> is declared by the
/// adapter, so a second bundled vendor is one new adapter and no edit to this file. A guard that
/// grew an <c>||</c> per vendor is the design this one exists to avoid.
/// </para>
/// <para>
/// It runs from <c>AddAgentCoreAsync</c> rather than from the route extension, so a host that
/// forgets to map a route still learns its document is self-contradictory.
/// </para>
/// </remarks>
public static class CallSpeechPairing
{
    /// <summary>Checks that the speech vendor is compatible with the selected call transport.</summary>
    /// <param name="call">The <c>providers.call</c> block.</param>
    /// <param name="speech">The <c>providers.speech</c> block.</param>
    /// <param name="selectedCall">The adapter <c>providers.call.kind</c> selected.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ConfigurationLoadException">
    /// The transport carries text and <c>providers.speech.kind</c> names a different vendor.
    /// </exception>
    public static void Validate(
        CallProviderConfiguration call,
        VendorProviderConfiguration speech,
        ICallAdapter selectedCall)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(selectedCall);

        if (!selectedCall.CarriesText)
        {
            // The socket carries audio, so a separate speech vendor is exactly what is wanted and
            // the two kinds are expected to differ.
            return;
        }

        if (string.Equals(speech.Kind, call.Kind, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new ConfigurationLoadException(new ConfigurationError
        {
            Pointer = "/providers/speech/kind",
            Message =
                $"providers.call is kind: {call.Kind}, and that transport carries text, so it "
                + $"performs recognition and synthesis itself. providers.speech is kind: "
                + $"{speech.Kind}, which would never be asked to do anything. Set providers.speech "
                + $"to '{call.Kind}', or choose a call transport that carries audio.",
            Check = ConfigurationCheck.ReferenceResolution,
        });
    }
}

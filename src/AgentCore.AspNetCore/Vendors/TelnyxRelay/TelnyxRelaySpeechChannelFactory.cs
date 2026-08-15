using AgentCore.Application.Ports;
using AgentCore.Application.Speech;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay;

/// <summary>Opens a Telnyx relay channel, where one connection is both halves.</summary>
/// <remarks>
/// <para>
/// <b>Which of the two shapes this is, and why.</b> A relay connection is born from an accepted
/// WebSocket rather than from a caller, so there is no moment at which something above asks this
/// factory to go and open a call: by the time a channel could exist, the socket is already up and
/// the connection already owns it. The other shape on offer was to reach the connection through
/// <see cref="SpeechChannelContext"/>, and that context carries a call id and the host's own
/// per-call values and no vendor field at all — which is exactly what makes D8 hold, and exactly
/// why it cannot smuggle a connection through. So the entry point stays
/// <see cref="TelnyxRelayConnection.RunAsync(Microsoft.AspNetCore.Http.HttpContext, System.Net.WebSockets.WebSocket, TelnyxRelayOptions)"/>,
/// <c>MapTelnyxRelay</c> is untouched, and this factory is built with the connection it hands out.
/// </para>
/// <para>
/// Nothing wires it into the container yet, and nothing can even construct it yet: the connection
/// it takes has a private constructor and <see cref="TelnyxRelayConnection.RunAsync(Microsoft.AspNetCore.Http.HttpContext, System.Net.WebSockets.WebSocket, TelnyxRelayOptions)"/>
/// keeps the only instance to itself. That is deliberate, not an oversight. The caller that will
/// need a factory is the split adapter — a recognizer on one side and a synthesizer on the other —
/// which does open its channel on request; whichever of the two arrives first, that adapter or a
/// reason for this connection to hand itself out, is the change that gives this type its first
/// caller. It exists now so that adapter meets an interface with a bundled implementation already
/// behind it, and so the bundled case is written down as the ordinary one rather than the exception.
/// </para>
/// </remarks>
/// <param name="connection">The connection that accepted this call's socket, and that is both halves of it.</param>
internal sealed class TelnyxRelaySpeechChannelFactory(TelnyxRelayConnection connection) : ISpeechChannelFactory
{
    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="context"/> is read for nothing, and the call id in it least of all: this
    /// vendor names the call on its own setup frame, and the connection is already answering that
    /// call by the time anything here could run. A future factory that really does open a call —
    /// the split adapter's — is where those values start to matter.
    /// </remarks>
    public ValueTask<SpeechChannel> OpenAsync(
        SpeechChannelContext context,
        CancellationToken cancellationToken = default)
    {
        // The same object in both slots. Nothing above this line can tell, and nothing may ask.
        return ValueTask.FromResult(new SpeechChannel(connection, connection));
    }
}

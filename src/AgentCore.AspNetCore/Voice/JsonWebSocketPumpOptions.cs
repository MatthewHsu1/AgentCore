using System.Net.WebSockets;

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>What one pump may do, and for how long.</summary>
    /// <param name="MaxFrameBytes">The largest inbound message the pump accepts, in bytes.</param>
    /// <param name="IdleTimeout">How long the pump waits with no inbound message before it ends the conversation.</param>
    /// <param name="CloseTimeout">How long the close handshake may take before the socket is aborted.</param>
    /// <param name="ProtocolFault">
    /// Builds the exception the pump throws when the peer broke the contract. The adapter supplies it,
    /// so the pump raises the adapter's own exception type and the adapter's own close-status rules
    /// still recognise it.
    /// </param>
    /// <param name="LogUnknownFrameType">
    /// Logs a discriminator no case matched. The pump calls it once for the conversation, not once for the
    /// frame, and passes the name; the line itself names the vendor, which is why it is a callback.
    /// </param>
    /// <param name="LogRefusedFrameBody">
    /// Logs a known discriminator whose body would not bind, on the same once-for-the-conversation rule as
    /// <paramref name="LogUnknownFrameType"/>.
    /// </param>
    /// <param name="LogIdleTimeout">Logs that the idle deadline, rather than teardown, ended the conversation.</param>
    internal sealed record JsonWebSocketPumpOptions(
        int MaxFrameBytes,
        TimeSpan IdleTimeout,
        TimeSpan CloseTimeout,
        Func<WebSocketCloseStatus, string, Exception> ProtocolFault,
        Action<string> LogUnknownFrameType,
        Action<string> LogRefusedFrameBody,
        Action LogIdleTimeout);
}

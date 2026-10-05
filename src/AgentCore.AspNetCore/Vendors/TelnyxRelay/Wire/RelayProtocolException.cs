using System.Net.WebSockets;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay.Wire
{
    /// <summary>The relay broke the contract, and the socket must close with a reason.</summary>
    /// <param name="status">The close status the vendor should see.</param>
    /// <param name="message">Why the endpoint refused.</param>
    internal sealed class RelayProtocolException(WebSocketCloseStatus status, string message)
        : Exception(message)
    {
        /// <summary>Gets the status the socket closes with.</summary>
        public WebSocketCloseStatus Status { get; } = status;
    }
}

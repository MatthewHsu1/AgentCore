using Microsoft.AspNetCore.Http;

namespace AgentCore.AspNetCore.Voice.Ports
{
    /// <summary>
    /// The inbound route of a conversation vendor: what answers it, and how it knows the request came from the vendor.
    /// They come as one value, so no vendor can answer a route it does not check.
    /// </summary>
    /// <param name="Handler">Answers the route, once the caller passed <paramref name="IsVendor"/>.</param>
    /// <param name="IsVendor">
    /// Whether the request came from the vendor: its webhook signature, a shared key, whatever the vendor offers. It runs
    /// before the route picks an entry. It may read the body, and must leave it readable for <paramref name="Handler"/>.
    /// </param>
    public sealed record ConversationRoute(RequestDelegate Handler, Func<HttpContext, ValueTask<bool>> IsVendor)
    {
        /// <summary>
        /// Gets what completes once the check holds the secrets it needs. The host does not start before it does, so a
        /// missing secret stops the host and never leaves the route open.
        /// </summary>
        public Task Ready { get; init; } = Task.CompletedTask;
    }
}

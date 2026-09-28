namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>
    /// Tags a route AgentCore mapped, so the startup check finds every route that must learn its entry.
    /// </summary>
    internal sealed class AgentCoreRouteMetadata
    {
        /// <summary>Gets the one tag every AgentCore route shares.</summary>
        public static AgentCoreRouteMetadata Instance { get; } = new();

        private AgentCoreRouteMetadata()
        {
        }
    }
}

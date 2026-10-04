using AgentCore.Application.Hooks.Gates;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>Tags a route AgentCore mapped, with how requests reach it, so the startup check and the entry gate find it.</summary>
    internal sealed class AgentCoreRouteMetadata
    {
        private AgentCoreRouteMetadata(EntryTransport transport)
        {
            Transport = transport;
        }

        /// <summary>Gets the tag of an HTTP route (Responses).</summary>
        public static AgentCoreRouteMetadata Http { get; } = new(EntryTransport.Http);

        /// <summary>Gets the tag of a call route (the conversation socket).</summary>
        public static AgentCoreRouteMetadata Call { get; } = new(EntryTransport.Call);

        /// <summary>Gets how requests reach the route.</summary>
        public EntryTransport Transport { get; }
    }
}

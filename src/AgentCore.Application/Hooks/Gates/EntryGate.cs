using System.Security.Claims;

namespace AgentCore.Application.Hooks.Gates
{
    /// <summary>A request needs an entry. A hook chooses one or refuses the request.</summary>
    public sealed class EntryGate : HookGate
    {
        internal EntryGate(
            HookScope scope,
            ClaimsPrincipal? user,
            IReadOnlyDictionary<string, string> headers,
            string route,
            string? requestedEntry,
            EntryTransport transport)
            : base(scope)
        {
            ArgumentNullException.ThrowIfNull(headers);
            ArgumentNullException.ThrowIfNull(route);

            User = user;
            Headers = headers;
            Route = route;
            RequestedEntry = requestedEntry;
            Transport = transport;
        }

        /// <summary>Gets the authenticated user, or <see langword="null"/> when the request is anonymous.</summary>
        public ClaimsPrincipal? User { get; }

        /// <summary>
        /// Gets the HTTP request's headers, on every route. On a call route that is the handshake or webhook request;
        /// the call's own SIP headers reach <see cref="CallGate.Headers"/>.
        /// </summary>
        public IReadOnlyDictionary<string, string> Headers { get; }

        /// <summary>Gets the route pattern that matched.</summary>
        public string Route { get; }

        /// <summary>Gets the entry the URL named, or <see langword="null"/> when the route has no entry segment.</summary>
        public string? RequestedEntry { get; }

        /// <summary>Gets how the request arrived.</summary>
        public EntryTransport Transport { get; }

        internal string? Chosen { get; private set; }

        internal bool Refused { get; private set; }

        /// <summary>Serves the request with this entry. Terminal.</summary>
        /// <param name="entry">The name of an entry the host serves.</param>
        public void Choose(string entry)
        {
            ArgumentException.ThrowIfNullOrEmpty(entry);
            Stage(terminal: true, () => Chosen = entry);
        }

        /// <summary>Refuses the request. Terminal.</summary>
        public void Refuse()
        {
            Stage(terminal: true, () => Refused = true);
        }
    }
}

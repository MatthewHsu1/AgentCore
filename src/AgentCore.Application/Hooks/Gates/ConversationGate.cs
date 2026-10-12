using System.Security.Claims;

namespace AgentCore.Application.Hooks.Gates
{
    /// <summary>
    /// A request names a conversation, before AgentCore opens it or runs a turn on it. A hook refuses a caller who may
    /// not touch the conversation. Every turn on an HTTP route passes this gate, an approval answer too.
    /// </summary>
    public sealed class ConversationGate : HookGate
    {
        internal ConversationGate(
            HookScope scope,
            ClaimsPrincipal? user,
            IReadOnlyDictionary<string, string> headers,
            string route,
            string entry,
            string conversationId,
            EntryTransport transport,
            bool resuming)
            : base(scope)
        {
            ArgumentNullException.ThrowIfNull(headers);
            ArgumentNullException.ThrowIfNull(route);
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(conversationId);

            User = user;
            Headers = headers;
            Route = route;
            Entry = entry;
            ConversationId = conversationId;
            Transport = transport;
            Resuming = resuming;
        }

        /// <summary>Gets the authenticated user, or <see langword="null"/> when the request is anonymous.</summary>
        public ClaimsPrincipal? User { get; }

        /// <summary>Gets the HTTP request's headers. A header with several values joins them with a comma.</summary>
        public IReadOnlyDictionary<string, string> Headers { get; }

        /// <summary>Gets the route pattern that matched.</summary>
        public string Route { get; }

        /// <summary>Gets the entry the request runs.</summary>
        public string Entry { get; }

        /// <summary>Gets the conversation the request names. It may be anything the caller sent.</summary>
        public string ConversationId { get; }

        /// <summary>Gets how the request arrived.</summary>
        public EntryTransport Transport { get; }

        /// <summary>Gets whether the request answers an approval instead of sending words.</summary>
        public bool Resuming { get; }

        internal bool Refused { get; private set; }

        /// <summary>
        /// Refuses the request. Terminal. The caller gets the same answer as for a conversation that does not exist, so a
        /// caller cannot learn which ids name somebody else's conversation.
        /// </summary>
        public void Refuse()
        {
            Stage(terminal: true, () => Refused = true);
        }
    }
}

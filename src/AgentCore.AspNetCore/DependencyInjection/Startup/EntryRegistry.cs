using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;

namespace AgentCore.AspNetCore.DependencyInjection.Startup
{
    /// <summary>One factory and one agent shim per entry, over the one session owner shared by every entry.</summary>
    internal sealed class EntryRegistry : IConversationSessionRegistry
    {
        /// <summary>Creates the registry over the per-entry seams and the one session owner.</summary>
        /// <param name="factories">The session factories, keyed by entry name.</param>
        /// <param name="agents">The agent shims, keyed by entry name.</param>
        /// <param name="sessions">The one session owner, shared by every entry.</param>
        public EntryRegistry(
            IReadOnlyDictionary<string, IConversationSessionFactory> factories,
            IReadOnlyDictionary<string, AgentCoreAgent> agents,
            IConversationSessions sessions)
        {
            ArgumentNullException.ThrowIfNull(factories);
            ArgumentNullException.ThrowIfNull(agents);
            ArgumentNullException.ThrowIfNull(sessions);

            Factories = factories;
            Agents = agents;
            Sessions = sessions;
            Entries = [.. factories.Keys];
        }

        /// <summary>Gets the session factories, keyed by entry name.</summary>
        public IReadOnlyDictionary<string, IConversationSessionFactory> Factories { get; }

        /// <summary>Gets the agent shims, keyed by entry name.</summary>
        public IReadOnlyDictionary<string, AgentCoreAgent> Agents { get; }

        /// <inheritdoc/>
        public IConversationSessions Sessions { get; }

        /// <inheritdoc/>
        public IReadOnlyCollection<string> Entries { get; }

        /// <summary>Reads one entry's factory.</summary>
        /// <param name="entry">The entry key a route named.</param>
        /// <returns>The factory for that entry.</returns>
        /// <exception cref="InvalidOperationException">The entry is not declared.</exception>
        public IConversationSessionFactory ForFactory(string entry)
        {
            return For(Factories, entry);
        }

        /// <summary>Reads one entry's agent shim.</summary>
        /// <param name="entry">The entry key a route named.</param>
        /// <returns>The agent for that entry.</returns>
        /// <exception cref="InvalidOperationException">The entry is not declared.</exception>
        public AgentCoreAgent ForAgent(string entry)
        {
            return For(Agents, entry);
        }

        /// <summary>Builds the failure text for an entry nothing declares, in one place.</summary>
        /// <param name="entry">The entry key a route named.</param>
        /// <param name="valid">The declared entry names.</param>
        /// <returns>The message both the startup check and the per-request backstop carry.</returns>
        internal static string UnknownEntryMessage(string entry, IEnumerable<string> valid)
        {
            return $"The entry '{entry}' is not declared. Valid entries: {string.Join(", ", valid)}.";
        }

        private static T For<T>(IReadOnlyDictionary<string, T> map, string entry)
        {
            ArgumentException.ThrowIfNullOrEmpty(entry);

            return map.TryGetValue(entry, out T? value) ? value : throw new InvalidOperationException(UnknownEntryMessage(entry, map.Keys));
        }
    }
}

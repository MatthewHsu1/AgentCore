using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;

namespace AgentCore.AspNetCore.DependencyInjection.Startup;

/// <summary>One factory, one agent shim, and one session store per entry.</summary>
/// <remarks>
/// Each entry compiles its own shape over the shared pool, so each entry gets its own
/// session factory and its own store: a vendor call id arriving on two routes opens two
/// isolated conversations rather than one conversation read through two shapes. The audit chain, the
/// observers, and the workspace root are shared across entries, because they describe the
/// deployment rather than the shape. Agents are named by entry key.
/// </remarks>
internal sealed class EntryRegistry : IConversationSessionRegistry
{
    /// <summary>Creates the registry over the per-entry seams.</summary>
    /// <param name="factories">The session factories, keyed by entry name.</param>
    /// <param name="agents">The agent shims, keyed by entry name.</param>
    /// <param name="conversationSessions">The session stores, keyed by entry name.</param>
    public EntryRegistry(
        IReadOnlyDictionary<string, IConversationSessionFactory> factories,
        IReadOnlyDictionary<string, AgentCoreAgent> agents,
        IReadOnlyDictionary<string, IConversationSessions> conversationSessions)
    {
        ArgumentNullException.ThrowIfNull(factories);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(conversationSessions);

        Factories = factories;
        Agents = agents;
        ConversationSessions = conversationSessions;
        Entries = conversationSessions.Keys.ToArray();
    }

    /// <summary>Gets the session factories, keyed by entry name.</summary>
    public IReadOnlyDictionary<string, IConversationSessionFactory> Factories { get; }

    /// <summary>Gets the agent shims, keyed by entry name.</summary>
    public IReadOnlyDictionary<string, AgentCoreAgent> Agents { get; }

    /// <summary>Gets the session stores, keyed by entry name.</summary>
    public IReadOnlyDictionary<string, IConversationSessions> ConversationSessions { get; }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> Entries { get; }

    /// <summary>Reads one entry's factory.</summary>
    /// <param name="entry">The entry key a route named.</param>
    /// <returns>The factory for that entry.</returns>
    /// <exception cref="InvalidOperationException">The entry is not declared.</exception>
    public IConversationSessionFactory ForFactory(string entry)
        => For(Factories, entry);

    /// <summary>Reads one entry's agent shim.</summary>
    /// <param name="entry">The entry key a route named.</param>
    /// <returns>The agent for that entry.</returns>
    /// <exception cref="InvalidOperationException">The entry is not declared.</exception>
    public AgentCoreAgent ForAgent(string entry)
        => For(Agents, entry);

    /// <inheritdoc/>
    public IConversationSessions ForSessions(string entry)
        => For(ConversationSessions, entry);

    /// <summary>Builds the failure text for an entry nothing declares, in one place.</summary>
    /// <param name="entry">The entry key a route named.</param>
    /// <param name="valid">The declared entry names.</param>
    /// <returns>The message both the startup check and the per-request backstop carry.</returns>
    internal static string UnknownEntryMessage(string entry, IEnumerable<string> valid)
        => $"The entry '{entry}' is not declared. Valid entries: {string.Join(", ", valid)}.";

    private static T For<T>(IReadOnlyDictionary<string, T> map, string entry)
    {
        ArgumentException.ThrowIfNullOrEmpty(entry);

        if (map.TryGetValue(entry, out var value))
        {
            return value;
        }

        throw new InvalidOperationException(UnknownEntryMessage(entry, map.Keys));
    }
}

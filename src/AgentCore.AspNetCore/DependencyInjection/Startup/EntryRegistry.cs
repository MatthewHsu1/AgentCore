using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;

namespace AgentCore.AspNetCore.DependencyInjection.Startup;

/// <summary>One factory, one agent shim, and one session store per entry.</summary>
/// <remarks>
/// Each entry compiles its own shape over the shared pool, so each entry gets its own
/// session factory and its own store: a vendor call id arriving on two routes opens two
/// isolated calls rather than one call read through two shapes. The audit chain, the
/// observers, and the workspace root are shared across entries, because they describe the
/// deployment rather than the shape. Agents are named by entry key.
/// </remarks>
internal sealed class EntryRegistry : ICallSessionRegistry
{
    /// <summary>Creates the registry over the per-entry seams.</summary>
    /// <param name="factories">The session factories, keyed by entry name.</param>
    /// <param name="agents">The agent shims, keyed by entry name.</param>
    /// <param name="callSessions">The session stores, keyed by entry name.</param>
    public EntryRegistry(
        IReadOnlyDictionary<string, ICallSessionFactory> factories,
        IReadOnlyDictionary<string, AgentCoreAgent> agents,
        IReadOnlyDictionary<string, ICallSessions> callSessions)
    {
        ArgumentNullException.ThrowIfNull(factories);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(callSessions);

        Factories = factories;
        Agents = agents;
        CallSessions = callSessions;
    }

    /// <summary>Gets the session factories, keyed by entry name.</summary>
    public IReadOnlyDictionary<string, ICallSessionFactory> Factories { get; }

    /// <summary>Gets the agent shims, keyed by entry name.</summary>
    public IReadOnlyDictionary<string, AgentCoreAgent> Agents { get; }

    /// <summary>Gets the session stores, keyed by entry name.</summary>
    public IReadOnlyDictionary<string, ICallSessions> CallSessions { get; }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> Entries => CallSessions.Keys.ToArray();

    /// <summary>Reads one entry's factory.</summary>
    /// <param name="entry">The entry key a route named.</param>
    /// <returns>The factory for that entry.</returns>
    /// <exception cref="InvalidOperationException">The entry is not declared.</exception>
    public ICallSessionFactory ForFactory(string entry)
        => For(Factories, entry);

    /// <summary>Reads one entry's agent shim.</summary>
    /// <param name="entry">The entry key a route named.</param>
    /// <returns>The agent for that entry.</returns>
    /// <exception cref="InvalidOperationException">The entry is not declared.</exception>
    public AgentCoreAgent ForAgent(string entry)
        => For(Agents, entry);

    /// <inheritdoc/>
    public ICallSessions ForSessions(string entry)
        => For(CallSessions, entry);

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

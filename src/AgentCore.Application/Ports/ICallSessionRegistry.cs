namespace AgentCore.Application.Ports;

/// <summary>
/// Finds the session store of one entry.
/// </summary>
public interface ICallSessionRegistry
{
    /// <summary>Gets the names of the entries the document declares.</summary>
    IReadOnlyCollection<string> Entries { get; }

    /// <summary>Reads one entry's session store.</summary>
    /// <param name="entry">The entry key, as the document declares it.</param>
    /// <returns>The store for that entry.</returns>
    /// <exception cref="InvalidOperationException">The entry is not declared.</exception>
    ICallSessions ForSessions(string entry);
}

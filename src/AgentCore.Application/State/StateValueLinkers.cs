namespace AgentCore.Application.State;

/// <summary>
/// The map from a <c>vocabulary.linker</c> name to the <see cref="IStateValueLinker"/> behind it
/// (K12). Always seeded with <c>exact</c> (K9) — the only linker that ships — so a host that binds
/// none still has a working registry.
/// </summary>
internal sealed class StateValueLinkers
{
    private readonly Dictionary<string, IStateValueLinker> _byName = new(StringComparer.Ordinal);

    /// <summary>Seeds the registry with <c>exact</c> and every linker a host registered.</summary>
    /// <param name="linkers">The linkers a host bound with <c>UseStateValueLinkers</c>.</param>
    /// <exception cref="ArgumentException">Two linkers, built-in or host-supplied, share a name.</exception>
    internal StateValueLinkers(IEnumerable<IStateValueLinker> linkers)
    {
        ArgumentNullException.ThrowIfNull(linkers);

        Add(new ExactStateValueLinker());
        foreach (var linker in linkers)
        {
            Add(linker);
        }
    }

    /// <summary>Gets every name registered, for <see cref="Configuration.Validation.ConfigurationValidator.ValidateLinkerNames"/>.</summary>
    internal IReadOnlySet<string> Names => _byName.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>Resolves the linker one name points at.</summary>
    /// <param name="name">A slot's <c>vocabulary.linker</c> value.</param>
    /// <returns>The linker.</returns>
    /// <exception cref="KeyNotFoundException">No linker is registered under that name.</exception>
    internal IStateValueLinker Resolve(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _byName.TryGetValue(name, out var linker)
            ? linker
            : throw new KeyNotFoundException($"no linker is registered under the name '{name}'.");
    }

    private void Add(IStateValueLinker linker)
    {
        ArgumentNullException.ThrowIfNull(linker);

        if (!_byName.TryAdd(linker.Name, linker))
        {
            throw new ArgumentException($"the linker name '{linker.Name}' is already registered.", nameof(linker));
        }
    }
}

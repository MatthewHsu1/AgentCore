using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// Holds each compiled entry for the life of the process.
/// </summary>
public sealed class CompiledAgentRegistry
{
    internal readonly record struct EntryKey(AgentCoreConfiguration Configuration, string Entry);

    private readonly Dictionary<EntryKey, CompiledAgent> _compiled = [];

    private readonly Lock _gate = new();
    
    private int _compileCount;

    /// <summary>Gets how many entries this registry has compiled.</summary>
    public int CompileCount
    {
        get
        {
            lock (_gate)
            {
                return _compileCount;
            }
        }
    }

    /// <summary>Gets the compiled agent for one entry, compiling it the first time only.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="entryName">The entry key.</param>
    /// <param name="context">The seams the document names. It is read on the first call only.</param>
    /// <returns>The one compiled agent for that entry.</returns>
    /// <exception cref="ConfigurationLoadException">The entry name is not declared.</exception>
    public CompiledAgent GetOrCompile(
        AgentCoreConfiguration configuration,
        string entryName,
        AgentCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(entryName);
        ArgumentNullException.ThrowIfNull(context);

        lock (_gate)
        {
            ThrowWhenUnknownEntry(configuration, entryName);

            var key = new EntryKey(configuration, entryName);
            if (_compiled.TryGetValue(key, out var existing))
            {
                return existing;
            }

            // The compile runs inside the lock, so 26 simultaneous callers still compile once.
            CacheAllLocked(configuration, context);
            return _compiled[key];
        }
    }

    /// <summary>Compiles every entry of one document, so boot fails fast on a bad entry.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="context">The seams the document names. It is read on the first call only.</param>
    /// <returns>The compiled agents, keyed by entry name.</returns>
    public IReadOnlyDictionary<string, CompiledAgent> EnsureAll(
        AgentCoreConfiguration configuration,
        AgentCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);

        lock (_gate)
        {
            CacheAllLocked(configuration, context);

            Dictionary<string, CompiledAgent> compiled = new(StringComparer.Ordinal);
            foreach (var name in configuration.Entries.Keys)
            {
                compiled[name] = _compiled[new EntryKey(configuration, name)];
            }

            return compiled;
        }
    }

    private void CacheAllLocked(AgentCoreConfiguration configuration, AgentCompilationContext context)
    {
        var missing = false;
        foreach (var name in configuration.Entries.Keys)
        {
            if (!_compiled.ContainsKey(new EntryKey(configuration, name)))
            {
                missing = true;
                break;
            }
        }

        if (!missing)
        {
            return;
        }

        var compiled = ConfigurationCompiler.CompileAll(configuration, context);
        foreach (var (name, agent) in compiled)
        {
            if (_compiled.TryAdd(new EntryKey(configuration, name), agent))
            {
                _compileCount++;
            }
        }
    }

    private static void ThrowWhenUnknownEntry(AgentCoreConfiguration configuration, string entryName)
    {
        if (!configuration.Entries.ContainsKey(entryName))
        {
            throw ConfigurationCompiler.Fail(
                "/entries",
                $"the entry '{entryName}' is not declared. Valid entries: {string.Join(", ", configuration.Entries.Keys)}.");
        }
    }
}

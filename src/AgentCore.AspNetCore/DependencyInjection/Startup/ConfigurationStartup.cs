using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Steps 1 and 2: read the one document the options name, and check it.</summary>
internal static class ConfigurationStartup
{
    /// <summary>Loads and validates the one document the options name.</summary>
    /// <param name="options">The options the host filled.</param>
    /// <returns>
    /// The loaded document, which has passed every check of section 8.5, tool references included.
    /// </returns>
    /// <exception cref="InvalidOperationException">The options name no document, or name two.</exception>
    /// <exception cref="ConfigurationLoadException">The document fails one of the checks.</exception>
    /// <remarks>
    /// Checks 2 to 8 report every defect at once, so one start names them all.
    /// </remarks>
    internal static AgentCoreConfiguration Load(AgentCoreOptions options)
    {
        var configuration = LoadDocument(options);
        ConfigurationValidator.ValidateStructure(configuration);

        // Decision 15 moved tool-reference resolution out of the structural pass so it can run
        // against MCP-discovered ids too. Nothing wires it in after discovery yet, so this call
        // restores the pre-split behaviour in the meantime: it resolves against the ids tools:
        // declares, the same set Evaluate uses. A later task moves this call to after discovery,
        // passing the registry's served ids so an MCP-served id can satisfy a reference too.
        ConfigurationValidator.ValidateToolReferences(
            configuration, configuration.Tools.Select(static tool => tool.Id).ToHashSet(StringComparer.Ordinal));

        return configuration;
    }

    /// <summary>Reads the one document the options name.</summary>
    /// <param name="options">The options the host filled.</param>
    /// <returns>The loaded document.</returns>
    /// <exception cref="InvalidOperationException">The options name no document, or name two.</exception>
    private static AgentCoreConfiguration LoadDocument(AgentCoreOptions options)
    {
        var hasPath = options.ConfigurationPath is { Length: > 0 };
        if (options.Configuration is { } loaded)
        {
            if (hasPath)
            {
                throw new InvalidOperationException(
                    "AddAgentCoreAsync names two documents: options.Configuration holds one and "
                    + "options.ConfigurationPath names another. Set one of the two.");
            }

            return loaded;
        }

        if (!hasPath)
        {
            throw new InvalidOperationException(
                "AddAgentCoreAsync names no document. Set options.ConfigurationPath to a .yaml, .yml, or .json "
                + "file, or set options.Configuration to a document the host already loaded.");
        }

        return ConfigurationLoader.LoadFile(options.ConfigurationPath!);
    }
}

using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>What <see cref="ConfigurationStartup.Load"/> hands back: the document, and the warnings its validation carried.</summary>
/// <param name="Configuration">The loaded document. See <see cref="ConfigurationStartup.Load"/> for what it has already passed.</param>
/// <param name="Warnings">
/// Every partial-coverage warning check 5 raises, and section 10's two ambiguity-and-vocabulary
/// warnings (K33, K39). <see cref="ConfigurationStartup"/> no longer discards these — a caller logs
/// them once a logger exists.
/// </param>
internal readonly record struct ConfigurationLoadResult(
    AgentCoreConfiguration Configuration, IReadOnlyList<ConfigurationError> Warnings);

/// <summary>Steps 1 and 2: read the one document the options name, and check its structure.</summary>
internal static class ConfigurationStartup
{
    /// <summary>Loads the one document the options name, and runs every structural check on it.</summary>
    /// <param name="options">The options the host filled.</param>
    /// <returns>
    /// The loaded document, which has passed every check of section 8.5 except tool-reference
    /// resolution, and every warning that check raised. Decision 15 runs tool-reference resolution
    /// after discovery, in the composition root, against the ids the tool registry actually serves —
    /// so an MCP-discovered id can satisfy a reference.
    /// </returns>
    /// <exception cref="InvalidOperationException">The options name no document, or name two.</exception>
    /// <exception cref="ConfigurationLoadException">The document fails one of the structural checks.</exception>
    /// <remarks>
    /// The structural checks report every defect at once, so one start names them all.
    /// </remarks>
    internal static ConfigurationLoadResult Load(AgentCoreOptions options)
    {
        var configuration = LoadDocument(options);
        var result = ConfigurationValidator.ValidateStructure(configuration);
        return new ConfigurationLoadResult(configuration, result.Warnings);
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

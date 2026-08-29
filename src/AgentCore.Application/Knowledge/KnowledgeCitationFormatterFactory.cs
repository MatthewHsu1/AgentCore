using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Picks the formatter <c>providers.knowledge.citation</c> names.
/// </summary>
public static class KnowledgeCitationFormatterFactory
{
    /// <summary>The JSON Pointer an unknown name reports.</summary>
    private const string Pointer = "/providers/knowledge/citation";

    /// <summary>Resolves the formatter the document names, or the shipped one.</summary>
    /// <param name="configuration">The loaded document. It carries <c>providers.knowledge.citation</c>.</param>
    /// <param name="registered">What the host bound with <c>UseKnowledgeCitationFormatters</c>.</param>
    /// <returns>The formatter every agent that declares <c>citations: true</c> writes through.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    /// <exception cref="ConfigurationLoadException">No formatter answers to the name the document wrote.</exception>
    public static IKnowledgeCitationFormatter Resolve(
        AgentCoreConfiguration configuration,
        IReadOnlyList<IKnowledgeCitationFormatter>? registered)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var name = configuration.Providers?.Knowledge?.Citation
            ?? KnowledgeProviderConfiguration.DefaultCitation;

        // The host's own registrations win, so a deployment can replace the shipped wording under
        // its own name without the framework knowing that name exists.
        var candidates = registered is { Count: > 0 }
            ? [.. registered, new SourceLocatorCitationFormatter()]
            : new List<IKnowledgeCitationFormatter> { new SourceLocatorCitationFormatter() };

        return candidates.FirstOrDefault(one => string.Equals(one.Name, name, StringComparison.Ordinal))
            ?? throw new ConfigurationLoadException(new ConfigurationError
            {
                Pointer = Pointer,
                Message = $"providers.knowledge.citation is '{name}', and no registered "
                    + $"{nameof(IKnowledgeCitationFormatter)} answers to it. This host offers "
                    + $"{string.Join(", ", candidates.Select(one => $"'{one.Name}'"))}. Register one "
                    + "with options.UseKnowledgeCitationFormatters, or name one of those.",
                Check = ConfigurationCheck.ReferenceResolution,
            });
    }
}

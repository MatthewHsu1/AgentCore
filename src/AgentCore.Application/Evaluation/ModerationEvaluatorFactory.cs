using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI.Evaluation;

namespace AgentCore.Application.Evaluation;

/// <summary>
/// Builds the moderation evaluator the document names, from the adapters the host registered.
/// </summary>
/// <remarks>
/// <para>
/// This is the moderation mirror of <c>CompositeKnowledgeStoreFactory</c>, and it takes the same
/// shape: the host lists the vendors it supports once, <c>providers.moderation.kind</c> picks one,
/// and a document that changes vendors changes no code.
/// </para>
/// <para>
/// A document that names no <c>providers.moderation</c> gets no evaluator, and a host that
/// registered no adapter for a kind the document does name fails the start. Both are deliberate: the
/// first is a document that asked for nothing, and the second is a document that asked for something
/// this host cannot give.
/// </para>
/// </remarks>
public static class ModerationEvaluatorFactory
{
    /// <summary>Builds the evaluator <c>providers.moderation</c> names, or none.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="adapters">The vendors this host supports.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>
    /// The evaluator, or <see langword="null"/> when the document names no moderation provider.
    /// </returns>
    /// <exception cref="ArgumentNullException">The configuration or the adapters are <see langword="null"/>.</exception>
    /// <exception cref="ConfigurationLoadException">
    /// The document names a <c>kind</c> no adapter serves, or a <c>kind</c> two adapters answer to.
    /// </exception>
    public static async ValueTask<IEvaluator?> CreateAsync(
        AgentCoreConfiguration configuration,
        ISecretResolverPort? secrets,
        IReadOnlyList<IModerationAdapter> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(adapters);

        if (configuration.Providers?.Moderation is not { } entry)
        {
            // The document asked for nothing, so no adapter is asked to build anything and no
            // credential is read. A host that registers a vendor still starts with no key.
            return null;
        }

        Dictionary<string, List<IModerationAdapter>> byKind = new(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
        {
            if (!byKind.TryGetValue(adapter.Kind, out var same))
            {
                same = [];
                byKind[adapter.Kind] = same;
            }

            same.Add(adapter);
        }

        if (!byKind.TryGetValue(entry.Kind, out var matching))
        {
            throw Fail(
                $"providers.moderation is kind: {entry.Kind}, and this host registers "
                + $"{Registered(byKind)}. Register an adapter for that kind, or change the document.");
        }

        if (matching.Count > 1)
        {
            throw Fail(
                $"two adapters answer to the kind '{entry.Kind}', so providers.moderation names two "
                + "endpoints. Register one adapter for each kind.");
        }

        return await matching[0].CreateAsync(entry, secrets, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the registered kinds, so a failure names what the host does register.</summary>
    private static string Registered(Dictionary<string, List<IModerationAdapter>> byKind)
        => byKind.Count == 0 ? "no adapter" : string.Join(", ", byKind.Keys.Select(kind => "'" + kind + "'"));

    /// <summary>Builds the one exception every failure of this factory uses.</summary>
    private static ConfigurationLoadException Fail(string message)
        => new(new ConfigurationError
        {
            Pointer = "/providers/moderation/kind",
            Message = message,
            Check = ConfigurationCheck.ReferenceResolution,
        });
}

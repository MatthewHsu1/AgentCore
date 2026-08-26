using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Providers;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// The composite a config-driven host binds its knowledge port through. It routes
/// <c>providers.knowledge.kind</c> to the <see cref="IKnowledgeStoreAdapter"/> whose kind matches.
/// </summary>
public static class CompositeKnowledgeStoreFactory
{
    /// <summary>What the knowledge field calls itself, so the shared selector writes its failures.</summary>
    private static readonly VendorSeam KnowledgeSeam =
        new("providers.knowledge.kind", "/providers/knowledge/kind", "options.UseKnowledgeStores(...)", "stores");

    /// <summary>Builds the knowledge port the document names.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="secrets">The chain the adapter resolves its credential through, or <see langword="null"/>.</param>
    /// <param name="adapters">The adapters the host registers, one for each vendor it supports.</param>
    /// <param name="embeddings">
    /// The generator <c>providers.embeddings</c> built, or <see langword="null"/> when the document
    /// names none. Forwarded to the matched adapter unread.
    /// </param>
    /// <param name="scopeDeclared">
    /// Whether ANY agent in the document declares <c>knowledge: { scoped: true }</c>. When
    /// <see langword="true"/>, the matched adapter must report <see cref="IKnowledgeStoreAdapter.CanScope"/>,
    /// or the start fails rather than search every customer's cards. This governs only that
    /// capability check; it is not the same question as <paramref name="requireScope"/>.
    /// </param>
    /// <param name="requireScope">
    /// Whether EVERY agent in the document declares <c>knowledge: { scoped: true }</c>, forwarded to
    /// <see cref="IKnowledgeStoreAdapter.CreateSearchAsync"/> as the store's own runtime guard. It is
    /// deliberately ANY vs ALL: one store is shared by every agent that reads it, so the store can
    /// only fail closed on a missing ambient scope when every reader wants one -- a mixed deployment
    /// must leave the store permissive and enforce scoping per agent one layer up instead.
    /// </param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The port.</returns>
    /// <exception cref="ConfigurationLoadException">
    /// <c>providers.knowledge.kind</c> names a kind no adapter serves, a kind two adapters answer
    /// to, or a kind whose adapter cannot apply a scope an agent declares.
    /// </exception>
    public static async ValueTask<IKnowledgeRetrievalPort?> CreateAsync(
        AgentCoreConfiguration configuration,
        ISecretResolverPort? secrets,
        IReadOnlyList<IKnowledgeStoreAdapter> adapters,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings,
        bool scopeDeclared,
        bool requireScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(adapters);

        var entry = configuration.Providers?.Knowledge ?? new KnowledgeProviderConfiguration();
        var adapter = Resolve(adapters, entry.Kind, scopeDeclared);

        return await adapter
            .CreateSearchAsync(entry, secrets, embeddings, requireScope, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Finds the one adapter <c>providers.knowledge.kind</c> names, and proves it serves.</summary>
    /// <param name="adapters">The adapters the host registers, one for each vendor it supports.</param>
    /// <param name="kind">The kind the document wrote.</param>
    /// <param name="scopeDeclared">Whether any agent in the document declares <c>scoped: true</c>.</param>
    /// <returns>The adapter.</returns>
    private static IKnowledgeStoreAdapter Resolve(
        IReadOnlyList<IKnowledgeStoreAdapter> adapters,
        string kind,
        bool scopeDeclared)
    {
        var adapter = VendorAdapterSelector.Select(kind, adapters, KnowledgeSeam);

        if (!adapter.CanServeSearch)
        {
            throw Fail(
                KnowledgeSeam.Pointer,
                $"{KnowledgeSeam.DocumentPath} names kind '{kind}', and that adapter does not serve "
                + $"{nameof(IKnowledgeRetrievalPort)}. This host registers {Registered(adapters)}. "
                + "Name a kind that serves this port.");
        }

        if (scopeDeclared && !adapter.CanScope)
        {
            throw Fail(
                KnowledgeSeam.Pointer,
                $"{KnowledgeSeam.DocumentPath} names kind '{kind}', an agent declares scoped: true, and that "
                + "adapter cannot apply a scope. A search that ignores a scope serves every customer "
                + "every card. Name a kind that scopes, or set scoped: false on every agent.");
        }

        return adapter;
    }

    /// <summary>Writes the registered kinds, so a failure names what the host does register.</summary>
    /// <param name="adapters">The adapters the host registers, one for each vendor it supports.</param>
    /// <returns>The kinds, or a phrase for a host with none.</returns>
    private static string Registered(IReadOnlyList<IKnowledgeStoreAdapter> adapters)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> kinds = [];
        foreach (var adapter in adapters)
        {
            if (seen.Add(adapter.Kind))
            {
                kinds.Add("'" + adapter.Kind + "'");
            }
        }

        return kinds.Count == 0 ? "no adapter" : string.Join(", ", kinds);
    }

    /// <summary>Builds the one exception every failure of this factory uses.</summary>
    /// <param name="pointer">The JSON Pointer into the document.</param>
    /// <param name="message">What is wrong.</param>
    /// <returns>The exception.</returns>
    private static ConfigurationLoadException Fail(string pointer, string message)
        => new(new ConfigurationError
        {
            Pointer = pointer,
            Message = message,
            Check = ConfigurationCheck.ReferenceResolution,
        });
}

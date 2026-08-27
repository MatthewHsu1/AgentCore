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

        if (entry.Mapper is null
            && CitationsDeclared(configuration)
            && entry.Fields.Source is not { Length: > 0 }
            && entry.Fields.Locator is not { Length: > 0 })
        {
            throw Fail(
                "/providers/knowledge/fields/source",
                "an agent declares knowledge: { citations: true } and providers.knowledge.fields maps "
                + "neither source nor locator, so every citation would be silently empty. Map one of "
                + "the two fields, or set citations: false on every agent.");
        }

        var adapter = Resolve(adapters, entry.Kind, scopeDeclared);

        return await adapter
            .CreateSearchAsync(entry, secrets, embeddings, requireScope, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Finds the one adapter <c>providers.knowledge.kind</c> names, and proves it serves.</summary>
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

    /// <summary>Whether any agent in the document turns citations on, directly or through defaults.</summary>
    private static bool CitationsDeclared(AgentCoreConfiguration configuration)
        => configuration.Agents is { } agents
            && (agents.Defaults?.Knowledge?.Citations == true
                || agents.Items.Any(agent => agent.Knowledge?.Citations == true));

    /// <summary>Writes the registered kinds, so a failure names what the host does register.</summary>
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
    private static ConfigurationLoadException Fail(string pointer, string message)
        => new(new ConfigurationError
        {
            Pointer = pointer,
            Message = message,
            Check = ConfigurationCheck.ReferenceResolution,
        });
}

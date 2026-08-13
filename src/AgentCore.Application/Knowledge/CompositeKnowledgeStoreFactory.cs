using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// The composite a config-driven host binds its two knowledge ports through. It routes
/// <c>providers.knowledge.search</c> and <c>providers.knowledge.documents</c> to the
/// <see cref="IKnowledgeStoreAdapter"/> whose kind matches.
/// </summary>
/// <remarks>
/// <para>
/// This is the knowledge mirror of <c>CompositeChatClientFactory</c>. The vendor half lives in the
/// adapters, one for each <c>kind</c>, and the vendor-neutral half lives here: the kind map, the two
/// defaults, and the one store two fields of one kind share. The document alone therefore decides
/// which vendor answers which port.
/// </para>
/// <para>
/// Every port a caller asks for is built while <see cref="CreateAsync"/> runs. A <c>kind</c> no
/// adapter serves, a <c>kind</c> two adapters answer to, and a <c>kind</c> whose adapter does not
/// serve the port that named it all stop the host at startup, and not on the first call. No adapter
/// is asked to build anything until every asked-for field found its adapter, so a failed start opens
/// no store.
/// </para>
/// <para>
/// A caller that already holds one of the two ports asks for the other alone, and the field it did
/// not ask for is read nowhere: its kind is never looked up, so a document that names a kind this
/// host does not register still starts when nothing was going to use that kind. That is what makes
/// the <c>UseKnowledgeStores</c> precedence rule cost nothing.
/// </para>
/// <para>
/// A document that names one kind for both asked-for fields gets one store:
/// <see cref="IKnowledgeStoreAdapter.CreateSearchAsync"/> runs, and the object it returns is bound
/// to the document port too when it also implements <see cref="IDocumentStorePort"/>. That is the
/// rule the <c>UseKnowledge</c> overload held in its <c>Once</c> local, and the file store is the
/// store it exists for.
/// </para>
/// </remarks>
public static class CompositeKnowledgeStoreFactory
{
    /// <summary>The field that names the ranking adapter.</summary>
    private const string SearchField = "search";

    /// <summary>The field that names the document adapter.</summary>
    private const string DocumentsField = "documents";

    /// <summary>Builds the knowledge ports of one document that the caller asks for, now.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="secrets">The chain each adapter resolves its credential through, or <see langword="null"/>.</param>
    /// <param name="adapters">The adapters the host registers, one for each vendor it supports.</param>
    /// <param name="includeSearch">
    /// Whether to read <c>providers.knowledge.search</c> and build the ranking port.
    /// <see langword="false"/> when the caller already holds that port, and then the field is neither
    /// resolved nor built.
    /// </param>
    /// <param name="includeDocuments">
    /// Whether to read <c>providers.knowledge.documents</c> and build the document port.
    /// <see langword="false"/> when the caller already holds that port, and then the field is neither
    /// resolved nor built.
    /// </param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>
    /// The ranking port and the document port. Each one is built when the caller asked for it and is
    /// <see langword="null"/> when the caller did not. Two asked-for ports are the same object when
    /// one kind serves both fields and that object answers both ports.
    /// </returns>
    /// <exception cref="ConfigurationLoadException">
    /// An asked-for field names a <c>kind</c> no adapter serves, a <c>kind</c> two adapters answer
    /// to, or a <c>kind</c> whose adapter does not serve that port.
    /// </exception>
    public static async ValueTask<(IKnowledgeRetrievalPort? Search, IDocumentStorePort? Documents)> CreateAsync(
        AgentCoreConfiguration configuration,
        ISecretResolverPort? secrets,
        IReadOnlyList<IKnowledgeStoreAdapter> adapters,
        bool includeSearch,
        bool includeDocuments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(adapters);

        if (!includeSearch && !includeDocuments)
        {
            // The caller holds both ports already, so this document has nothing left to say.
            return (null, null);
        }

        // The kind is a vendor name, and a vendor name is written by a human. It matches without
        // regard to case, and every other name in the document stays ordinal. Two adapters may land
        // on one key, and the field that names that key is the one that reports it.
        Dictionary<string, List<IKnowledgeStoreAdapter>> byKind = new(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
        {
            if (!byKind.TryGetValue(adapter.Kind, out var same))
            {
                same = [];
                byKind[adapter.Kind] = same;
            }

            same.Add(adapter);
        }

        // A document with no knowledge block still binds both ports, because both fields default to
        // the file store. Section 7 says the knowledge base is always there.
        var entry = configuration.Providers?.Knowledge ?? new KnowledgeProviderConfiguration();

        // Every asked-for lookup runs before any build, so a document that names one good kind and
        // one bad one opens nothing at all. A field nobody asked for is read nowhere.
        var searchAdapter = includeSearch
            ? Resolve(byKind, entry.Search, SearchField, nameof(IKnowledgeRetrievalPort), documents: false)
            : null;
        var documentsAdapter = includeDocuments
            ? Resolve(byKind, entry.Documents, DocumentsField, nameof(IDocumentStorePort), documents: true)
            : null;

        IKnowledgeRetrievalPort? search = null;
        if (searchAdapter is not null)
        {
            search = await searchAdapter
                .CreateSearchAsync(entry, secrets, cancellationToken)
                .ConfigureAwait(false);
        }

        if (documentsAdapter is null)
        {
            return (search, null);
        }

        // One kind for both fields is one store, whenever that store reads as well.
        if (ReferenceEquals(searchAdapter, documentsAdapter) && search is IDocumentStorePort both)
        {
            return (search, both);
        }

        var read = await documentsAdapter
            .CreateDocumentsAsync(entry, secrets, cancellationToken)
            .ConfigureAwait(false);

        return (search, read);
    }

    /// <summary>Finds the one adapter a field names, and proves it serves that port.</summary>
    /// <param name="byKind">The adapters, by the kind each serves.</param>
    /// <param name="kind">The kind the field names.</param>
    /// <param name="field">The field of <c>providers.knowledge</c> that named it.</param>
    /// <param name="port">The port that field binds, named for the message.</param>
    /// <param name="documents">Whether the field is <c>documents</c> rather than <c>search</c>.</param>
    /// <returns>The adapter.</returns>
    private static IKnowledgeStoreAdapter Resolve(
        Dictionary<string, List<IKnowledgeStoreAdapter>> byKind,
        string kind,
        string field,
        string port,
        bool documents)
    {
        var pointer = "/providers/knowledge/" + field;

        if (!byKind.TryGetValue(kind, out var same))
        {
            throw Fail(
                pointer,
                $"providers.knowledge.{field} is kind: {kind}, and this host registers "
                + $"{Registered(byKind)}. Register an adapter for that kind, or change the document.");
        }

        if (same.Count > 1)
        {
            throw Fail(
                pointer,
                $"two adapters answer to the kind '{kind}', so providers.knowledge.{field} names two "
                + "stores. Register one adapter for each kind.");
        }

        var adapter = same[0];
        var serves = documents ? adapter.CanServeDocuments : adapter.CanServeSearch;
        return serves
            ? adapter
            : throw Fail(
                pointer,
                $"providers.knowledge.{field} names kind '{kind}', and that adapter does not serve "
                + $"{port}. This host registers {Registered(byKind)}. Name a kind that serves this port.");
    }

    /// <summary>Writes the registered kinds, so a failure names what the host does register.</summary>
    /// <param name="byKind">The adapters, by the kind each serves.</param>
    /// <returns>The kinds, or a phrase for a host with none.</returns>
    private static string Registered(Dictionary<string, List<IKnowledgeStoreAdapter>> byKind)
        => byKind.Count == 0 ? "no adapter" : string.Join(", ", byKind.Keys.Select(kind => "'" + kind + "'"));

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

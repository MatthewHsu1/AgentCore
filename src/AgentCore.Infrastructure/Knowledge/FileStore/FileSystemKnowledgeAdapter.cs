using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Knowledge.FileStore;

/// <summary>
/// The <c>filesystem</c> knowledge vendor: a directory tree of text files, behind both ports.
/// </summary>
/// <remarks>
/// <para>
/// This is the default of both <c>providers.knowledge.search</c> and
/// <c>providers.knowledge.documents</c>, so a document that names no store still reads its knowledge
/// base from <c>./kb</c>. <see cref="FileSystemKnowledgeStore"/> ranks and reads, so one store
/// answers both ports and this adapter opens it once.
/// </para>
/// <para>
/// The store holds no connection and no credential, so the build reads
/// <c>providers.knowledge.root</c> and nothing else.
/// </para>
/// </remarks>
public sealed class FileSystemKnowledgeAdapter : IKnowledgeStoreAdapter
{
    /// <summary>The <c>kind</c> value this adapter serves.</summary>
    public const string ProviderKind = "filesystem";

    private FileSystemKnowledgeStore? _store;

    /// <summary>Gets the one <c>kind</c> value this adapter serves.</summary>
    public string Kind => ProviderKind;

    /// <summary>Gets <see langword="true"/>: the file store ranks its own passages.</summary>
    public bool CanServeSearch => true;

    /// <summary>Gets <see langword="true"/>: the file store reads its own documents.</summary>
    public bool CanServeDocuments => true;

    /// <summary>Opens the tree the document names, and ranks over it.</summary>
    /// <param name="entry">The <c>providers.knowledge</c> block. Only <c>root</c> is read.</param>
    /// <param name="secrets">Unused: a directory needs no credential.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The store.</returns>
    public ValueTask<IKnowledgeRetrievalPort> CreateSearchAsync(
        KnowledgeProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IKnowledgeRetrievalPort>(Open(entry));

    /// <summary>Opens the tree the document names, and reads from it.</summary>
    /// <param name="entry">The <c>providers.knowledge</c> block. Only <c>root</c> is read.</param>
    /// <param name="secrets">Unused: a directory needs no credential.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The store, which is the same object <see cref="CreateSearchAsync"/> returns.</returns>
    public ValueTask<IDocumentStorePort> CreateDocumentsAsync(
        KnowledgeProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IDocumentStorePort>(Open(entry));

    /// <summary>Opens the one store this adapter owns, once.</summary>
    /// <param name="entry">The <c>providers.knowledge</c> block.</param>
    /// <returns>The store.</returns>
    /// <remarks>
    /// Both create methods run while the host starts and on one thread, so the two ports of one
    /// document always share one tree and one walk of it.
    /// </remarks>
    private FileSystemKnowledgeStore Open(KnowledgeProviderConfiguration entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _store ??= new FileSystemKnowledgeStore(entry);
    }
}

using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Ports;

/// <summary>
/// Builds the knowledge ports behind one <c>providers.knowledge.search</c> or
/// <c>providers.knowledge.documents</c> value.
/// </summary>
/// <remarks>
/// <para>
/// This is the knowledge mirror of <see cref="IChatClientAdapter"/>. The document names a vendor and
/// the host registers one adapter for each vendor it supports. <c>CompositeKnowledgeStoreFactory</c>
/// routes each of the two fields to the adapter whose <see cref="Kind"/> matches, so a document that
/// changes stores changes no code.
/// </para>
/// <para>
/// The two ports pick their adapter one at a time, so one document ranks in a vector store and still
/// reads its pages from disk. A vendor that serves only one half says so in
/// <see cref="CanServeSearch"/> and <see cref="CanServeDocuments"/>, and the composite fails the
/// start when the document asks it for the other half. The create method of a half this adapter does
/// not serve throws <see cref="NotSupportedException"/>.
/// </para>
/// <para>
/// An adapter owns its vendor only: the SDK client, the credential, the collection name. Everything
/// vendor-neutral — the kind map, the defaults, and the one store two fields of one kind share —
/// lives in the composite, so no adapter repeats it.
/// </para>
/// </remarks>
public interface IKnowledgeStoreAdapter
{
    /// <summary>Gets the one <c>kind</c> value this adapter serves, such as <c>filesystem</c>.</summary>
    /// <remarks>A vendor name is written by a human, so it matches without regard to case.</remarks>
    string Kind { get; }

    /// <summary>Gets whether this adapter answers <see cref="IKnowledgeRetrievalPort"/>.</summary>
    bool CanServeSearch { get; }

    /// <summary>Gets whether this adapter answers <see cref="IDocumentStorePort"/>.</summary>
    bool CanServeDocuments { get; }

    /// <summary>Builds the ranking half of the knowledge base.</summary>
    /// <param name="entry">The <c>providers.knowledge</c> block, whose <c>search</c> named this adapter.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The port. The host owns it for the life of the process.</returns>
    /// <exception cref="NotSupportedException"><see cref="CanServeSearch"/> is <see langword="false"/>.</exception>
    /// <remarks>
    /// This runs once, while the host starts. A bad credential therefore stops the host, never a call.
    /// An adapter that also reads returns one object that answers both ports, and the composite then
    /// binds that one object twice.
    /// </remarks>
    ValueTask<IKnowledgeRetrievalPort> CreateSearchAsync(
        KnowledgeProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default);

    /// <summary>Builds the document half of the knowledge base.</summary>
    /// <param name="entry">The <c>providers.knowledge</c> block, whose <c>documents</c> named this adapter.</param>
    /// <param name="secrets">The chain a credential resolves through, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The port. The host owns it for the life of the process.</returns>
    /// <exception cref="NotSupportedException"><see cref="CanServeDocuments"/> is <see langword="false"/>.</exception>
    /// <remarks>
    /// This runs once, while the host starts, and it does not run at all when one kind serves both
    /// fields and the ranked object reads as well.
    /// </remarks>
    ValueTask<IDocumentStorePort> CreateDocumentsAsync(
        KnowledgeProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default);
}

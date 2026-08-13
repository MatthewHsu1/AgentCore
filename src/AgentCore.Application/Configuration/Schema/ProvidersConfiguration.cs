namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// One large language model, and the name a <see cref="ModelReference"/> points at.
/// </summary>
public sealed record LlmProviderConfiguration
{
    /// <summary>Gets the vendor, such as <c>openai</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the model name the vendor knows.</summary>
    public required string Model { get; init; }

    /// <summary>Gets the name this entry answers to, such as <c>reply</c> or <c>fill</c>.</summary>
    public required string As { get; init; }
}

/// <summary>
/// A provider that names one vendor and nothing else, such as speech or telephony.
/// </summary>
public sealed record VendorProviderConfiguration
{
    /// <summary>Gets the vendor, such as <c>telnyx-relay</c>.</summary>
    public required string Kind { get; init; }
}

/// <summary>
/// The knowledge provider: one adapter for each knowledge port, and what those adapters read.
/// </summary>
/// <remarks>
/// The two ports pick their adapter one at a time, so a document can search a vector store and
/// still read the documents from disk.
/// </remarks>
public sealed record KnowledgeProviderConfiguration
{
    /// <summary>The search adapter used when the document names none.</summary>
    public const string DefaultSearch = "filesystem";

    /// <summary>The document adapter used when the document names none.</summary>
    public const string DefaultDocuments = "filesystem";

    /// <summary>The root used when the document sets none.</summary>
    public const string DefaultRoot = "./kb";

    /// <summary>The collection used when the document names none.</summary>
    public const string DefaultCollection = "kb_chunks";

    /// <summary>Gets the adapter that answers <c>IKnowledgeRetrievalPort</c>, such as <c>zilliz</c>.</summary>
    public string Search { get; init; } = DefaultSearch;

    /// <summary>Gets the adapter that answers <c>IDocumentStorePort</c>, such as <c>filesystem</c>.</summary>
    public string Documents { get; init; } = DefaultDocuments;

    /// <summary>Gets the root of the knowledge-base tree. The tree is its own Git repository.</summary>
    public string Root { get; init; } = DefaultRoot;

    /// <summary>Gets the cluster URL the <c>zilliz</c> adapter reads, or <see langword="null"/>.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Gets the collection the <c>zilliz</c> adapter reads.</summary>
    public string Collection { get; init; } = DefaultCollection;
}

/// <summary>
/// The <c>providers:</c> section. It configures adapters and never changes agent shape.
/// </summary>
/// <remarks>
/// This is the seam where the ports in section 7 bind.
/// </remarks>
public sealed record ProvidersConfiguration
{
    /// <summary>Gets the language models, in document order.</summary>
    public EquatableList<LlmProviderConfiguration> Llm { get; init; } = EquatableList<LlmProviderConfiguration>.Empty;

    /// <summary>Gets the speech provider, or <see langword="null"/>.</summary>
    public VendorProviderConfiguration? Speech { get; init; }

    /// <summary>Gets the telephony provider, or <see langword="null"/>.</summary>
    public VendorProviderConfiguration? Telephony { get; init; }

    /// <summary>Gets the moderation provider, or <see langword="null"/>.</summary>
    /// <remarks>
    /// <c>AgentCore.Application.Ports.IModerationAdapter</c> reads it, and the host registers one
    /// adapter for each vendor it supports. A document that names none moderates nothing, and every
    /// turn reaches the model.
    /// </remarks>
    public VendorProviderConfiguration? Moderation { get; init; }

    /// <summary>Gets the knowledge provider, or <see langword="null"/>.</summary>
    public KnowledgeProviderConfiguration? Knowledge { get; init; }
}

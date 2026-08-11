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
/// The knowledge provider: the vector store and the root of the document tree.
/// </summary>
public sealed record KnowledgeProviderConfiguration
{
    /// <summary>The root used when the document sets none.</summary>
    public const string DefaultRoot = "./kb";

    /// <summary>Gets the vector store, such as <c>zilliz</c>.</summary>
    public required string Store { get; init; }

    /// <summary>Gets the root of the knowledge-base tree. The tree is its own Git repository.</summary>
    public string Root { get; init; } = DefaultRoot;
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

    /// <summary>Gets the knowledge provider, or <see langword="null"/>.</summary>
    public KnowledgeProviderConfiguration? Knowledge { get; init; }
}

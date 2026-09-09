namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// The embedding provider: the vendor that turns a query into a vector, and the model it uses.
/// </summary>
public sealed record EmbeddingProviderConfiguration
{
    /// <summary>Gets the vendor, such as <c>openai</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the model name the vendor knows, such as <c>text-embedding-3-small</c>.</summary>
    public required string Model { get; init; }

    /// <summary>
    /// Gets the vector width to ask the vendor for, or <see langword="null"/> for the model's own.
    /// It must match the width the knowledge collection was built with.
    /// </summary>
    public int? Dimensions { get; init; }
}

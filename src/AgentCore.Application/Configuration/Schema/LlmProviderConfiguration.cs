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

    /// <summary>
    /// Gets how hard a reasoning model thinks before it answers, or <see langword="null"/> to send
    /// nothing and let the vendor decide.
    /// </summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>
    /// Gets whether this entry may run a hosted web search, or <see langword="null"/> to let the
    /// vendor's adapter decide.
    /// </summary>
    public bool? WebSearch { get; init; }
}

namespace AgentCore.Application.Configuration.Schema;

/// <summary>When the extractor runs. Section 8.3 defines one value.</summary>
public enum ExtractorTrigger
{
    /// <summary>The extractor runs against the finished turn, while the caller speaks. This is D21.</summary>
    AfterReply,
}

/// <summary>
/// A pointer to one entry of <c>providers.llm</c>, with the call settings that go with it.
/// </summary>
public sealed record ModelReference
{
    /// <summary>Gets the <c>as</c> name of the provider entry, such as <c>reply</c> or <c>fill</c>.</summary>
    public required string Ref { get; init; }

    /// <summary>Gets the sampling temperature, or <see langword="null"/> when the document sets none.</summary>
    public double? Temperature { get; init; }
}

/// <summary>
/// The state extractor. It is the bridge from prose to typed state.
/// </summary>
/// <remarks>
/// The extractor never speaks, holds no tools, and decides nothing. The guards decide.
/// </remarks>
public sealed record ExtractorConfiguration
{
    /// <summary>Gets the model the extractor calls.</summary>
    public required ModelReference Model { get; init; }

    /// <summary>Gets when the extractor runs. It defaults to <see cref="ExtractorTrigger.AfterReply"/>.</summary>
    public ExtractorTrigger When { get; init; } = ExtractorTrigger.AfterReply;
}

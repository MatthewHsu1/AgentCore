namespace AgentCore.Domain.Sources;

/// <summary>
/// One thing the caller is shown as the origin of an answer.
/// </summary>
public sealed record SourceReference
{
    /// <summary>Gets the id of this source, unique within one turn. Publishing it twice shows it once.</summary>
    public required string SourceId { get; init; }

    /// <summary>Gets which of the two shapes this source takes.</summary>
    public required SourceKind Kind { get; init; }

    /// <summary>Gets what this source is called, as the caller should read it.</summary>
    public required string Title { get; init; }

    /// <summary>Gets what produced this source, such as <c>knowledge</c>. Free text the screen may group by.</summary>
    public required string Origin { get; init; }

    /// <summary>Gets where inside the source it sits, such as <c>p.27</c>. Empty when the producer has none.</summary>
    public string Locator { get; init; } = string.Empty;

    /// <summary>Gets the link the caller may open, or <see langword="null"/> when there is nothing to open.</summary>
    public string? Url { get; init; }

    /// <summary>Gets the media type of the source, for a producer that knows one.</summary>
    public string MediaType { get; init; } = "text/plain";
}

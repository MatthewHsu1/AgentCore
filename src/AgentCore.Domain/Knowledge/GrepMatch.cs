namespace AgentCore.Domain.Knowledge;

/// <summary>
/// One line of one knowledge-base document that a grep pattern matched.
/// </summary>
public sealed record GrepMatch
{
    /// <summary>Gets the document the line comes from. <c>knowledge.read</c> opens it.</summary>
    public required string DocumentId { get; init; }

    /// <summary>Gets where the line sits in the document. The first line of a document is line 1.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the line itself, without the line ending that closes it.</summary>
    public required string Line { get; init; }
}

namespace AgentCore.Application.Calls;

/// <summary>One page of a listing.</summary>
/// <param name="Calls">The page's rows, most recently active first.</param>
/// <param name="NextCursor">
/// What to pass as <c>after</c> for the following page, or <see langword="null"/> when this page was
/// the last. A full page always carries one; a short page never does.
/// </param>
public sealed record CallPage(IReadOnlyList<CallRecord> Calls, string? NextCursor);

using System.Text.Json;

namespace AgentCore.Application.Calls;

/// <summary>One call, apart from its words. It is one row of store 0.</summary>
/// <param name="CallId">The call this describes. It is the join to stores 1 and 3.</param>
/// <param name="Title">What to show in a list, or <see langword="null"/> until one is made.</param>
/// <param name="Status">Whether the call is still listed as usual.</param>
/// <param name="ExternalId">A consumer's own id for the call, or <see langword="null"/>.</param>
/// <param name="Custom">A consumer's own fields, or <see langword="null"/>.</param>
/// <param name="CreatedAt">When the call row was made. UTC.</param>
/// <param name="LastMessageAt">
/// When store 1 last wrote a word of this call, or <see langword="null"/> when it holds none. UTC.
/// Derived at read time; store 0 keeps no such column.
/// </param>
public sealed record CallRecord(
    string CallId,
    string? Title,
    CallStatus Status,
    string? ExternalId,
    JsonElement? Custom,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastMessageAt);

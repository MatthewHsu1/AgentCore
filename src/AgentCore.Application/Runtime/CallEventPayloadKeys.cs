using AgentCore.Domain.Audit;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The payload keys a diagnostic-only <see cref="CallEvent"/> carries.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AuditPayloadKeys"/> names every key the chain of D23 stores, and it is a permanent
/// promise: a stored row is read years later, so a key there is stable forever. The four diagnostic
/// kinds store nothing. Their detail lives for the length of one dispatch and reaches a counter and a
/// log line, so it is keyed here instead, where the name promises nothing outside this process.
/// </para>
/// <para>
/// A key the chain already names is NOT repeated here. <see cref="CallEventKind.ToolFailed"/> carries
/// its message under <see cref="AuditPayloadKeys.ToolError"/> and
/// <see cref="CallEventKind.PromptFlagged"/> its categories under
/// <see cref="AuditPayloadKeys.ModerationCategories"/>, because both of those facts are also stored,
/// and one fact under two keys is two facts to a reader.
/// </para>
/// </remarks>
internal static class CallEventPayloadKeys
{
    /// <summary>Why a diagnostic-only event happened, in the words the turn loop used.</summary>
    /// <remarks>
    /// It is the text <c>Log.ExtractionFailed</c> and <c>Log.ModerationUnavailable</c> report, and
    /// nothing counts it: the closed vocabulary an operator alerts on is the metric attribute beside
    /// the line, so this stays free text.
    /// </remarks>
    public const string Reason = "reason";
}

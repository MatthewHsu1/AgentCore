using AgentCore.Domain.Audit;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The payload keys a diagnostic-only <see cref="CallEvent"/> carries.
/// </summary>
internal static class CallEventPayloadKeys
{
    /// <summary>
    /// Why a diagnostic-only event happened, in the words the turn loop used.
    /// </summary>
    public const string Reason = "reason";
}

using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>
/// Every line <c>AddAgentCoreAsync</c> writes while it wires the container.
/// </summary>
internal static partial class StartupLog
{
    /// <summary>The document named no <c>providers.audit</c>, so the in-process store was opened.</summary>
    /// <param name="logger">The factory's logger for the audit queue.</param>
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "the document names no providers.audit, so the audit chain of D23 is kept in this "
            + "process. That store is not durable and it grows without a bound. Name a durable "
            + "providers.audit.kind, or write kind: memory to say this was meant.")]
    public static partial void AuditSinkDefaulted(ILogger logger);

    /// <summary>The document named no <c>providers.calls</c>, so the in-process store was opened.</summary>
    /// <param name="logger">The factory's logger for the in-process store.</param>
    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "the document names no providers.calls, so a call's row and the words of every "
            + "call are kept in this process. That store is not durable, it grows without a bound, "
            + "and no retention window applies to it. Name a durable providers.calls.kind, or write "
            + "kind: memory to say this was meant.")]
    public static partial void CallStoreDefaulted(ILogger logger);
}

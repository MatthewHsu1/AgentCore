using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection
{
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

        /// <summary>The document named no <c>providers.conversations</c>, so the in-process store was opened.</summary>
        /// <param name="logger">The factory's logger for the in-process store.</param>
        [LoggerMessage(
            EventId = 3,
            Level = LogLevel.Warning,
            Message = "the document names no providers.conversations, so a conversation's row and the words of every "
                + "conversation are kept in this process. That store is not durable, it grows without a bound, "
                + "and no retention window applies to it. Name a durable providers.conversations.kind, or write "
                + "kind: memory to say this was meant.")]
        public static partial void ConversationStoreDefaulted(ILogger logger);

        /// <summary>A <c>providers.conversation.filler</c> key names no tool the registry serves.</summary>
        /// <param name="logger">The boot's own logger.</param>
        /// <param name="pointer">The JSON Pointer to the filler key.</param>
        /// <param name="toolId">The key, as the document wrote it.</param>
        [LoggerMessage(
            EventId = 4,
            Level = LogLevel.Warning,
            Message = "{Pointer}: the filler key '{ToolId}' names no tool the registry serves, so its filler never "
                + "fires unless a tool of that name is added at run time. The shell, files, memory, skills, and "
                + "search providers name their tools only while a turn runs, so this check cannot see them. Check "
                + "the key for a typo.")]
        public static partial void FillerToolUnknown(ILogger logger, string pointer, string toolId);
    }
}

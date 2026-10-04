using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Hooks.Engine
{
    /// <summary>Every line the hook engine writes. Each names the hook type and the gate or notice.</summary>
    internal static partial class HookLog
    {
        /// <summary>A gate hook threw.</summary>
        /// <param name="logger">The engine's logger.</param>
        /// <param name="hookType">The full name of the hook's type.</param>
        /// <param name="gate">The gate point's name.</param>
        /// <param name="conversationId">The id of the conversation, empty before one exists.</param>
        /// <param name="exception">What the hook threw.</param>
        [LoggerMessage(
            EventId = 200,
            Level = LogLevel.Warning,
            Message = "The hook {HookType} failed at gate {Gate} of conversation {ConversationId}. Its verbs were dropped.")]
        public static partial void GateFailed(ILogger logger, string hookType, string gate, string conversationId, Exception exception);

        /// <summary>A gate hook missed its deadline and was abandoned.</summary>
        /// <param name="logger">The engine's logger.</param>
        /// <param name="hookType">The full name of the hook's type.</param>
        /// <param name="gate">The gate point's name.</param>
        /// <param name="conversationId">The id of the conversation, empty before one exists.</param>
        /// <param name="deadlineMs">The gate's deadline, in milliseconds.</param>
        [LoggerMessage(
            EventId = 201,
            Level = LogLevel.Warning,
            Message = "The hook {HookType} missed the {DeadlineMs} ms deadline of gate {Gate} of conversation {ConversationId}. It was abandoned and its verbs were dropped.")]
        public static partial void GateAbandoned(ILogger logger, string hookType, string gate, string conversationId, long deadlineMs);

        /// <summary>A notice hook threw.</summary>
        /// <param name="logger">The engine's logger.</param>
        /// <param name="hookType">The full name of the hook's type.</param>
        /// <param name="noticeType">The notice's type name.</param>
        /// <param name="conversationId">The id of the conversation, empty for a host notice.</param>
        /// <param name="exception">What the hook threw.</param>
        [LoggerMessage(
            EventId = 202,
            Level = LogLevel.Warning,
            Message = "The hook {HookType} failed on the notice {NoticeType} of conversation {ConversationId}.")]
        public static partial void NoticeFailed(ILogger logger, string hookType, string noticeType, string conversationId, Exception exception);

        /// <summary>A notice hook passed its NoticeTimeout and the delivery was abandoned.</summary>
        /// <param name="logger">The engine's logger.</param>
        /// <param name="hookType">The full name of the hook's type.</param>
        /// <param name="noticeType">The notice's type name.</param>
        /// <param name="conversationId">The id of the conversation, empty for a host notice.</param>
        /// <param name="timeoutMs">The hook's notice timeout, in milliseconds.</param>
        [LoggerMessage(
            EventId = 203,
            Level = LogLevel.Warning,
            Message = "The hook {HookType} did not finish the notice {NoticeType} of conversation {ConversationId} within {TimeoutMs} ms. The delivery was abandoned.")]
        public static partial void NoticeAbandoned(ILogger logger, string hookType, string noticeType, string conversationId, long timeoutMs);

        /// <summary>A notice hook with no timeout is still working on one notice.</summary>
        /// <param name="logger">The engine's logger.</param>
        /// <param name="hookType">The full name of the hook's type.</param>
        /// <param name="noticeType">The notice's type name.</param>
        /// <param name="conversationId">The id of the conversation, empty for a host notice.</param>
        [LoggerMessage(
            EventId = 204,
            Level = LogLevel.Warning,
            Message = "The hook {HookType} is still working on the notice {NoticeType} of conversation {ConversationId}. Its later notices wait behind it.")]
        public static partial void NoticeStillWaiting(ILogger logger, string hookType, string noticeType, string conversationId);

        /// <summary>The host stopped before every notice was delivered.</summary>
        /// <param name="logger">The engine's logger.</param>
        /// <param name="mailboxes">How many mailboxes were still draining.</param>
        /// <param name="timeoutMs">How long the stop waited, in milliseconds.</param>
        [LoggerMessage(
            EventId = 205,
            Level = LogLevel.Warning,
            Message = "The host stopped before the hooks of {Mailboxes} conversations finished their notices; it waited {TimeoutMs} ms.")]
        public static partial void DrainTimedOut(ILogger logger, int mailboxes, long timeoutMs);

        /// <summary>The engine itself failed while it delivered a notice; the hook's reader goes on with the next one.</summary>
        /// <param name="logger">The engine's logger.</param>
        /// <param name="hookType">The full name of the hook's type.</param>
        /// <param name="noticeType">The notice's type name.</param>
        /// <param name="conversationId">The id of the conversation, empty for a host notice.</param>
        /// <param name="exception">What went wrong.</param>
        [LoggerMessage(
            EventId = 206,
            Level = LogLevel.Error,
            Message = "AgentCore failed to deliver the notice {NoticeType} of conversation {ConversationId} to the hook {HookType}. The hook's later notices still go out.")]
        public static partial void DeliveryFailed(ILogger logger, string hookType, string noticeType, string conversationId, Exception exception);

        /// <summary>A hook kept denying tool calls past the approval layer's round cap, so the turn ends as a fault.</summary>
        /// <param name="logger">The engine's logger.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="turnIndex">The turn that ended.</param>
        /// <param name="maxRounds">How many model rounds of denials the layer answered.</param>
        [LoggerMessage(
            EventId = 207,
            Level = LogLevel.Warning,
            Message = "A hook denied tool calls of turn {TurnIndex} of conversation {ConversationId} after {MaxRounds} model rounds of denials. The turn ends as a fault, and no person is asked.")]
        public static partial void DenialRoundsExhausted(ILogger logger, string conversationId, int turnIndex, int maxRounds);
    }
}

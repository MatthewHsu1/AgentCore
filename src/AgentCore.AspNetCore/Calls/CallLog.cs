using AgentCore.Application.Hooks.Gates;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Calls
{
    /// <summary>The call core's log lines. None carries what anyone said.</summary>
    internal static partial class CallLog
    {
        [LoggerMessage(EventId = 300, Level = LogLevel.Information, Message = "the {Transport} call {CallId} was refused: {Refusal}.")]
        internal static partial void CallRefused(ILogger logger, string callId, string transport, CallRefusal refusal);

        [LoggerMessage(EventId = 301, Level = LogLevel.Warning, Message = "the call {CallId} named conversation {ConversationId}, which an entry other than {Entry} holds, so it was refused as busy.")]
        internal static partial void ConversationInUse(ILogger logger, string callId, string conversationId, string entry);

        [LoggerMessage(EventId = 302, Level = LogLevel.Error, Message = "ending the call {CallId} on conversation {ConversationId} failed.")]
        internal static partial void CallEndFaulted(ILogger logger, string callId, string conversationId, Exception fault);

        [LoggerMessage(EventId = 303, Level = LogLevel.Warning, Message = "reading the session of conversation {ConversationId} failed; the call keeps its own.")]
        internal static partial void SessionTouchFaulted(ILogger logger, string conversationId, Exception fault);

        [LoggerMessage(EventId = 304, Level = LogLevel.Warning, Message = "an ask of the call {CallId} failed, so the caller hears the fallback reply.")]
        internal static partial void AskFailed(ILogger logger, string callId, Exception fault);

        [LoggerMessage(EventId = 305, Level = LogLevel.Warning, Message = "the replaced ask of the call {CallId} did not stop within {Seconds} s; the new one starts anyway.")]
        internal static partial void OlderAskLingered(ILogger logger, string callId, double seconds);

        [LoggerMessage(EventId = 306, Level = LogLevel.Warning, Message = "the call {CallId} named conversation {ConversationId}, which another call holds, so it was refused as busy.")]
        internal static partial void ConversationHeldByCall(ILogger logger, string callId, string conversationId);

        [LoggerMessage(EventId = 307, Level = LogLevel.Warning, Message = "the call {CallId} lost conversation {ConversationId} to another call, so its spoken lines are dropped.")]
        internal static partial void LinesDropped(ILogger logger, string callId, string conversationId);

        [LoggerMessage(EventId = 308, Level = LogLevel.Warning, Message = "the open ask of the call {CallId} did not stop within {Seconds} s of the call's end; the end goes on anyway.")]
        internal static partial void AskLingeredAtEnd(ILogger logger, string callId, double seconds);

        [LoggerMessage(EventId = 309, Level = LogLevel.Information, Message = "a newer connection of the call {CallId} took conversation {ConversationId} over, so the older one is dropped.")]
        internal static partial void CallReplaced(ILogger logger, string callId, string conversationId);

        [LoggerMessage(EventId = 323, Level = LogLevel.Warning, Message = "the end notice of the call {CallId} could not be raised, so the hooks never hear it.")]
        internal static partial void CallEndNoticeLost(ILogger logger, string callId, Exception fault);
    }
}

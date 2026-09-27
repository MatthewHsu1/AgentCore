using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay
{
    /// <summary>
    /// Every line the relay connection writes.
    /// </summary>
    internal static partial class TelnyxRelayLog
    {
        /// <summary>Section 3.1. An unmodelled frame type, logged once for the conversation.</summary>
        /// <param name="logger">The logger of the connection.</param>
        /// <param name="frameType">The <c>type</c> value no case matched.</param>
        /// <param name="conversationId">The id of the conversation, or a placeholder before setup.</param>
        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Information,
            Message = "the relay sent an unmodelled frame type '{FrameType}' on conversation {ConversationId}.")]
        public static partial void UnknownFrameType(ILogger logger, string frameType, string conversationId);

        /// <summary>The caller pressed one key. The key itself never reaches a log line.</summary>
        /// <param name="logger">The logger of the connection.</param>
        [LoggerMessage(
            EventId = 2,
            Level = LogLevel.Information,
            Message = "the caller pressed a key.")]
        public static partial void DtmfReceived(ILogger logger);

        /// <summary>The vendor refused a frame this endpoint sent. That is our defect.</summary>
        /// <param name="logger">The logger of the connection.</param>
        /// <param name="conversationId">The id of the conversation, or a placeholder before setup.</param>
        /// <param name="description">Why the vendor refused it.</param>
        [LoggerMessage(
            EventId = 3,
            Level = LogLevel.Warning,
            Message = "the relay refused a frame on conversation {ConversationId}: {Description}")]
        public static partial void FrameRefused(ILogger logger, string conversationId, string description);

        /// <summary>The read loop ended in a fault rather than a clean close.</summary>
        /// <param name="logger">The logger of the connection.</param>
        /// <param name="conversationId">The id of the conversation, or a placeholder before setup.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 5,
            Level = LogLevel.Error,
            Message = "the relay read loop of conversation {ConversationId} ended in a fault.")]
        public static partial void ReadLoopFaulted(ILogger logger, string conversationId, Exception exception);

        /// <summary>The write loop ended in a fault rather than a clean close.</summary>
        /// <param name="logger">The logger of the connection.</param>
        /// <param name="conversationId">The id of the conversation, or a placeholder before setup.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 7,
            Level = LogLevel.Error,
            Message = "the relay write loop of conversation {ConversationId} ended in a fault.")]
        public static partial void WriteLoopFaulted(ILogger logger, string conversationId, Exception exception);

        /// <summary>A bounded teardown wait expired before the task it was watching stopped.</summary>
        /// <param name="logger">The logger of the connection.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="what">Which task did not stop in time, for example "the last turn".</param>
        [LoggerMessage(
            EventId = 8,
            Level = LogLevel.Error,
            Message = "{What} of conversation {ConversationId} did not stop before the close timeout.")]
        public static partial void TeardownTimedOut(ILogger logger, string conversationId, string what);

        /// <summary>The close handshake faulted for a reason other than its own deadline or a dead peer.</summary>
        /// <param name="logger">The logger of the connection.</param>
        /// <param name="conversationId">The id of the conversation, or a placeholder before setup.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 9,
            Level = LogLevel.Error,
            Message = "the close handshake of conversation {ConversationId} faulted.")]
        public static partial void CloseFaulted(ILogger logger, string conversationId, Exception exception);

        /// <summary>Cancelling the connection's own token faulted, from a registered callback.</summary>
        /// <param name="logger">The logger of the connection.</param>
        /// <param name="conversationId">The id of the conversation, or a placeholder before setup.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 10,
            Level = LogLevel.Error,
            Message = "cancelling the connection token of conversation {ConversationId} faulted.")]
        public static partial void CancellationFaulted(ILogger logger, string conversationId, Exception exception);

        /// <summary>A malformed interrupt frame was refused. Section 7.1: a bad frame must not drop the conversation.</summary>
        /// <param name="logger">The logger of the connection.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        [LoggerMessage(
            EventId = 13,
            Level = LogLevel.Warning,
            Message = "the relay sent a malformed interrupt frame on conversation {ConversationId}. It is dropped.")]
        public static partial void MalformedInterruptFrame(ILogger logger, string conversationId);

        /// <summary>The relay dropped the socket with no close frame. Task 7: an ordinary end of a conversation.</summary>
        /// <param name="logger">The logger of the connection.</param>
        /// <param name="conversationId">The id of the conversation, or a placeholder before setup.</param>
        [LoggerMessage(
            EventId = 15,
            Level = LogLevel.Information,
            Message = "conversation {ConversationId} ended without a close frame from the relay.")]
        public static partial void ConversationDroppedWithNoCloseFrame(ILogger logger, string conversationId);

        /// <summary>The read loop refused a frame and the connection is closing because of it.</summary>
        /// <param name="logger">The logger of the connection.</param>
        /// <param name="conversationId">The id of the conversation, or a placeholder before setup.</param>
        /// <param name="reason">
        /// The fixed, vendor-free message <see cref="RelayProtocolException"/> carries — never the
        /// bytes the relay actually sent.
        /// </param>
        [LoggerMessage(
            EventId = 16,
            Level = LogLevel.Warning,
            Message = "the relay read loop of conversation {ConversationId} closed the socket over a protocol violation: {Reason}")]
        public static partial void RelayProtocolViolation(ILogger logger, string conversationId, string reason);

        /// <summary>No inbound frame arrived within <c>IdleTimeout</c>. Task 8: an ordinary end of a conversation.</summary>
        /// <param name="logger">The logger of the connection.</param>
        /// <param name="conversationId">The id of the conversation, or a placeholder before setup.</param>
        [LoggerMessage(
            EventId = 17,
            Level = LogLevel.Information,
            Message = "conversation {ConversationId} ended: no inbound frame arrived before the idle deadline.")]
        public static partial void IdleTimeoutReached(ILogger logger, string conversationId);

        /// <summary>A frame of a known type carried a field that would not bind, logged once for the conversation.</summary>
        /// <param name="logger">The logger of the connection.</param>
        /// <param name="frameType">The <c>type</c> value whose body was refused.</param>
        /// <param name="conversationId">The id of the conversation, or a placeholder before setup.</param>
        [LoggerMessage(
            EventId = 18,
            Level = LogLevel.Warning,
            Message = "the relay sent a '{FrameType}' frame on conversation {ConversationId} whose body this build cannot "
                + "read. It is dropped, and the conversation continues.")]
        public static partial void FrameBodyRefused(ILogger logger, string frameType, string conversationId);

        /// <summary>The session of a conversation could not be closed at the end of that conversation.</summary>
        /// <param name="logger">The logger.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 22,
            Level = LogLevel.Error,
            Message = "the session of conversation {ConversationId} could not be closed, so its last words may never reach store 1 and it waits out the idle timeout.")]
        public static partial void ConversationCloseFaulted(ILogger logger, string conversationId, Exception exception);
    }
}

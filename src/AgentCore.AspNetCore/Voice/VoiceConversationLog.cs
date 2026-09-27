using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>
    /// Every line <see cref="VoiceConversationLoop"/> and the speech types under it write.
    /// </summary>
    internal static partial class VoiceConversationLog
    {
        /// <summary>A prompt arrived before the conversation started, logged once for the conversation.</summary>
        /// <param name="logger">The logger of the connection that owns the loop.</param>
        [LoggerMessage(
            EventId = 4,
            Level = LogLevel.Warning,
            Message = "the transport sent a prompt before the conversation started. Every prompt before the start is dropped.")]
        public static partial void PromptBeforeSetup(ILogger logger);

        /// <summary>The transport reported a barge-in. Never the words the caller said.</summary>
        /// <param name="logger">The logger of the connection that owns the loop.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        [LoggerMessage(
            EventId = 12,
            Level = LogLevel.Information,
            Message = "the transport reported a barge-in on conversation {ConversationId}.")]
        public static partial void InterruptReceived(ILogger logger, string conversationId);

        /// <summary>The conversation started a second time on one transport, logged once for the conversation.</summary>
        /// <param name="logger">The logger of the connection that owns the loop.</param>
        /// <param name="conversationId">The id of the conversation the first setup frame started.</param>
        [LoggerMessage(
            EventId = 19,
            Level = LogLevel.Warning,
            Message = "the transport started conversation {ConversationId} a second time. The first session is "
                + "released and the new one replaces it.")]
        public static partial void SecondSetupFrame(ILogger logger, string conversationId);

        /// <summary>Closing the audit chain of a conversation faulted, so that call has no <c>conversation.ended</c> event.</summary>
        /// <param name="logger">The logger of the connection that owns the loop.</param>
        /// <param name="conversationId">The id of the conversation whose chain did not close.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 20,
            Level = LogLevel.Error,
            Message = "the audit chain of conversation {ConversationId} could not be closed, so it has no conversation.ended event.")]
        public static partial void ConversationEndFaulted(ILogger logger, string conversationId, Exception exception);

        /// <summary>The store could not be told that a conversation is still being had.</summary>
        /// <param name="logger">The logger.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 23,
            Level = LogLevel.Warning,
            Message = "the store could not be read for conversation {ConversationId}, so that call may reach its idle timeout and be dropped while it is still running.")]
        public static partial void SessionTouchFaulted(ILogger logger, string conversationId, Exception exception);

        /// <summary>An interrupted speech did not finish within <see cref="SpeechHandle.InterruptionTimeout"/>, so its tasks were cancelled.</summary>
        /// <param name="logger">The logger the speech was created with.</param>
        /// <param name="speechId">The id of the speech.</param>
        /// <param name="timeoutSeconds">The timeout that passed, in seconds.</param>
        [LoggerMessage(
            EventId = 24,
            Level = LogLevel.Error,
            Message = "speech {SpeechId} not done in time after interruption ({TimeoutSeconds} s), cancelling the speech arbitrarily.")]
        public static partial void SpeechNotDoneAfterInterruption(ILogger logger, string speechId, double timeoutSeconds);

        /// <summary>A callback waiting for a speech to be done threw.</summary>
        /// <param name="logger">The logger the speech was created with.</param>
        /// <param name="speechId">The id of the speech.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 25,
            Level = LogLevel.Warning,
            Message = "error in a done callback of speech {SpeechId}.")]
        public static partial void SpeechDoneCallbackFaulted(ILogger logger, string speechId, Exception exception);

        /// <summary>An interruption stopped at a queued speech that disallows interruptions.</summary>
        /// <param name="logger">The logger of the voice session.</param>
        /// <param name="speechId">The id of the queued speech.</param>
        [LoggerMessage(
            EventId = 26,
            Level = LogLevel.Warning,
            Message = "a queued speech {SpeechId} does not allow interruptions and will play after the interruption, "
                + "use interrupt(force=True) to interrupt it as well")]
        public static partial void QueuedSpeechNotInterruptible(ILogger logger, string speechId);

        /// <summary>A speech was scheduled after the scheduling loop ended, so it was cancelled.</summary>
        /// <param name="logger">The logger of the voice session.</param>
        /// <param name="speechId">The id of the speech.</param>
        [LoggerMessage(
            EventId = 27,
            Level = LogLevel.Warning,
            Message = "attempting to schedule speech {SpeechId}, but the scheduling task is not running, the speech will be cancelled")]
        public static partial void SchedulingTaskNotRunning(ILogger logger, string speechId);

        /// <summary>A speech task faulted.</summary>
        /// <param name="logger">The logger of the voice session.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 28,
            Level = LogLevel.Error,
            Message = "error in a speech task.")]
        public static partial void SpeechTaskFaulted(ILogger logger, Exception exception);

        /// <summary>The speech scheduling loop faulted, so no further speech plays.</summary>
        /// <param name="logger">The logger of the voice session.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 29,
            Level = LogLevel.Error,
            Message = "error in the speech scheduling task.")]
        public static partial void SchedulingLoopFaulted(ILogger logger, Exception exception);

        /// <summary>A committed user turn arrived while the current speech disallows interruptions, so it was dropped.</summary>
        /// <param name="logger">The logger of the voice session.</param>
        [LoggerMessage(
            EventId = 30,
            Level = LogLevel.Warning,
            Message = "skipping reply to user input, current speech generation cannot be interrupted")]
        public static partial void UserTurnDroppedUninterruptibleSpeech(ILogger logger);

        /// <summary>A committed user turn arrived while speech scheduling is paused, so it was skipped.</summary>
        /// <param name="logger">The logger of the voice session.</param>
        [LoggerMessage(
            EventId = 31,
            Level = LogLevel.Warning,
            Message = "skipping user input, speech scheduling is paused")]
        public static partial void UserTurnSkippedSchedulingPaused(ILogger logger);

        /// <summary>A filler's own <c>say</c> raised, so no further filler will fire for that tool call.</summary>
        /// <param name="logger">The logger of the voice session.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 32,
            Level = LogLevel.Error,
            Message = "filler stopped on an error, no further filler will play for this tool call.")]
        public static partial void FillerFaulted(ILogger logger, Exception exception);

        /// <summary>The caller went away as the session closed, so the away prompt was not spoken.</summary>
        /// <param name="logger">The logger of the voice session.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 33,
            Level = LogLevel.Information,
            Message = "the away prompt was not spoken, speech scheduling had already stopped for the close.")]
        public static partial void UserAwayPromptNotScheduled(ILogger logger, Exception exception);

        /// <summary>The transport named a conversation another entry holds live, so nothing was opened.</summary>
        /// <param name="logger">The logger of the connection that owns the loop.</param>
        /// <param name="conversationId">The id of the conversation the transport named.</param>
        /// <param name="entry">The entry the transport arrived on.</param>
        [LoggerMessage(
            EventId = 36,
            Level = LogLevel.Warning,
            Message = "conversation {ConversationId} is in use by another entry, so entry {Entry} refused the transport.")]
        public static partial void ConversationInUse(ILogger logger, string conversationId, string entry);

        /// <summary>The engine refused a cut, so the turn's history stays as it was.</summary>
        /// <param name="logger">The logger of the voice session.</param>
        /// <param name="turnIndex">The turn the cut named.</param>
        [LoggerMessage(
            EventId = 34,
            Level = LogLevel.Information,
            Message = "the cut of turn {TurnIndex} was refused: the turn was already cut, or a later turn had started, so its history is unchanged.")]
        public static partial void CutRefused(ILogger logger, int turnIndex);

        /// <summary>The engine refused a recut, so the turn keeps the cut it took.</summary>
        /// <param name="logger">The logger of the voice session.</param>
        /// <param name="turnIndex">The turn the recut named.</param>
        [LoggerMessage(
            EventId = 35,
            Level = LogLevel.Information,
            Message = "the recut of turn {TurnIndex} was refused: the turn took no cut, or a later turn had started, so it keeps the cut it took.")]
        public static partial void RecutRefused(ILogger logger, int turnIndex);
    }
}

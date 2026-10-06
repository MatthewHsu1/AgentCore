using AgentCore.AspNetCore.Vendors.OpenAiLive.Webhook;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive
{
    /// <summary>The openai-live adapter's log lines. None carries what anyone said, nor a key.</summary>
    internal static partial class OpenAiLiveLog
    {
        [LoggerMessage(EventId = 310, Level = LogLevel.Warning, Message = "an openai-live webhook failed its signature check and was refused.")]
        internal static partial void SignatureRefused(ILogger logger);

        [LoggerMessage(EventId = 311, Level = LogLevel.Warning, Message = "OpenAI did not take the accept of call {CallId}: {Outcome}.")]
        internal static partial void AcceptRefused(ILogger logger, string callId, AcceptOutcome outcome);

        [LoggerMessage(EventId = 312, Level = LogLevel.Error, Message = "attaching to the call {CallId} failed, so it was hung up.")]
        internal static partial void AttachFailed(ILogger logger, string callId, Exception fault);

        [LoggerMessage(EventId = 313, Level = LogLevel.Error, Message = "the sideband of the call {CallId} failed.")]
        internal static partial void SidebandFaulted(ILogger logger, string callId, Exception fault);

        [LoggerMessage(EventId = 314, Level = LogLevel.Warning, Message = "GPT-Live reported an error on the call {CallId}: {Code}: {Detail}")]
        internal static partial void LiveError(ILogger logger, string callId, string code, string detail);

        [LoggerMessage(EventId = 315, Level = LogLevel.Error, Message = "answering a delegation of the call {CallId} failed.")]
        internal static partial void AnswerFaulted(ILogger logger, string callId, Exception fault);

        [LoggerMessage(EventId = 316, Level = LogLevel.Warning, Message = "hanging up the call {CallId} failed.")]
        internal static partial void HangupFailed(ILogger logger, string callId);

        [LoggerMessage(EventId = 317, Level = LogLevel.Warning, Message = "rejecting the call {CallId} failed.")]
        internal static partial void RejectFailed(ILogger logger, string callId);

        [LoggerMessage(EventId = 318, Level = LogLevel.Error, Message = "closing the session of the call {CallId} failed.")]
        internal static partial void CloseFaulted(ILogger logger, string callId, Exception fault);

        [LoggerMessage(EventId = 319, Level = LogLevel.Warning, Message = "GPT-Live did not ack the last answer of the call {CallId} within {Seconds} s, so the call is hung up anyway.")]
        internal static partial void AckLate(ILogger logger, string callId, double seconds);

        [LoggerMessage(EventId = 320, Level = LogLevel.Information, Message = "the call {CallId} is hung up from this side: {Cause}.")]
        internal static partial void HungUpBecause(ILogger logger, string callId, string cause);

        [LoggerMessage(EventId = 321, Level = LogLevel.Error, Message = "hanging up the call {CallId} threw, so it is ended anyway.")]
        internal static partial void HangupFaulted(ILogger logger, string callId, Exception fault);

        [LoggerMessage(EventId = 322, Level = LogLevel.Error, Message = "running the call {CallId} failed.")]
        internal static partial void RunFaulted(ILogger logger, string callId, Exception fault);

        [LoggerMessage(EventId = 324, Level = LogLevel.Information, Message = "the call {CallId} is being transferred.")]
        internal static partial void Transferring(ILogger logger, string callId);

        [LoggerMessage(EventId = 325, Level = LogLevel.Warning, Message = "transferring the call {CallId} failed: {Why}. The call is hung up.")]
        internal static partial void TransferFailed(ILogger logger, string callId, string why);

        [LoggerMessage(EventId = 326, Level = LogLevel.Error, Message = "sending the transfer of the call {CallId} threw.")]
        internal static partial void ReferFaulted(ILogger logger, string callId, Exception fault);

        [LoggerMessage(EventId = 327, Level = LogLevel.Warning, Message = "transferring the call {CallId} failed: {Why}. The caller is told, and the call goes on.")]
        internal static partial void TransferFailedGoingOn(ILogger logger, string callId, string why);

        [LoggerMessage(EventId = 328, Level = LogLevel.Warning, Message = "a fact for the voice of the call {CallId} could not be sent, so the voice goes on without it.")]
        internal static partial void VoiceContextFailed(ILogger logger, string callId, Exception fault);
    }
}

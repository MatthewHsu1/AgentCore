using AgentCore.Application.Diagnostics;
using AgentCore.Application.Hooks.Notices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Hooks.BuiltIn
{
    /// <summary>
    /// The "log once" failure lines, read from notices. It never writes what the caller or the agent said.
    /// </summary>
    /// <param name="logger">Where the lines go, or <see langword="null"/> for nowhere.</param>
    internal sealed class LoggingHook(ILogger? logger) : AgentHook
    {
        /// <summary>The turn a line names when the notice carried no index. No turn has it.</summary>
        private const int NoTurn = -1;

        private readonly ILogger _logger = logger ?? NullLogger.Instance;

        /// <inheritdoc />
        public override ValueTask OnTurnRefusedAsync(TurnRefused notice, CancellationToken cancellationToken)
        {
            Log.TurnRefused(_logger, IdOf(notice), TurnOf(notice), TurnRefusalTokens.ToToken(notice.Reason));
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public override ValueTask OnInputModeratedAsync(InputModerated notice, CancellationToken cancellationToken)
        {
            switch (notice.Verdict)
            {
                case InputVerdict.Flagged:
                    Log.PromptRefused(_logger, IdOf(notice), TurnOf(notice), string.Join(',', notice.Categories));
                    break;
                case InputVerdict.Unavailable:
                    Log.ModerationUnavailable(_logger, IdOf(notice), TurnOf(notice), notice.Reason == ModerationUnavailableReason.TimedOut
                        ? TurnFailureReasons.ModerationTimedOut
                        : TurnFailureReasons.ModerationFaulted);
                    break;
                case InputVerdict.Clean:
                default:
                    break;
            }

            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public override ValueTask OnTurnCompletedAsync(TurnCompleted notice, CancellationToken cancellationToken)
        {
            TurnFailureAccounting.Write(
                _logger, IdOf(notice), TurnOf(notice), TurnFailureAccounting.Of(notice.Outcome, notice.FailedInTool), notice.Cause);
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public override ValueTask OnFaultAsync(Fault notice, CancellationToken cancellationToken)
        {
            switch (notice)
            {
                case { Kind: FaultKind.ExtractionFailed }:
                    Log.ExtractionFailed(_logger, IdOf(notice), TurnOf(notice), notice.Message);
                    break;
                case { Kind: FaultKind.StateRestorePartial }:
                    Log.StateRestorePartial(_logger, IdOf(notice), notice.Message);
                    break;
                case { Kind: FaultKind.TranscriptWriteFailed, Cause: { } cause }:
                    Log.TranscriptWriteFailed(_logger, IdOf(notice), TurnOf(notice), cause);
                    break;
                case { Kind: FaultKind.TranscriptResyncFailed, Cause: { } cause }:
                    Log.TranscriptResyncFailed(_logger, IdOf(notice), TurnOf(notice), cause);
                    break;
                default:
                    // HookFailed is logged by the engine itself; the busy-mark faults keep their own direct lines.
                    break;
            }

            return ValueTask.CompletedTask;
        }

        private static string IdOf(HookNotice notice)
        {
            return notice.Scope.ConversationId ?? string.Empty;
        }

        private static int TurnOf(HookNotice notice)
        {
            return notice.Scope.TurnIndex ?? NoTurn;
        }
    }
}

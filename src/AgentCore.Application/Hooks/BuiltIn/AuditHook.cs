using System.Globalization;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Hooks.BuiltIn
{
    /// <summary>
    /// Turns the durable facts of a conversation into the append-only audit chain: the 8 audit kinds, with
    /// <see cref="HookNotice.EventId"/> as the row id. It never times out, so a slow store is waited out,
    /// and behind <c>QueuedAuditSink</c> a full queue makes it wait rather than drop.
    /// </summary>
    /// <param name="sink">Where the rows go.</param>
    /// <param name="logger">Where a refused row is reported.</param>
    internal sealed class AuditHook(IAuditSinkPort sink, ILogger logger) : AgentHook
    {
        private static readonly IReadOnlyDictionary<string, string> NoPayload = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <inheritdoc />
        public override TimeSpan? NoticeTimeout => null;

        /// <inheritdoc />
        public override ValueTask OnConversationStartedAsync(ConversationStarted notice, CancellationToken cancellationToken)
        {
            return AppendAsync(notice, AuditEventKind.ConversationStarted, NoPayload, amends: null, cancellationToken);
        }

        /// <inheritdoc />
        public override ValueTask OnConversationEndedAsync(ConversationEnded notice, CancellationToken cancellationToken)
        {
            Dictionary<string, string> payload = new(StringComparer.Ordinal) { [AuditPayloadKeys.EndReason] = ConversationEndReasons.ToToken(notice.Reason) };
            if (notice.TerminalStage is { Length: > 0 } stage)
            {
                payload[AuditPayloadKeys.StageAfter] = stage;
            }

            return AppendAsync(notice, AuditEventKind.ConversationEnded, payload, amends: null, cancellationToken);
        }

        /// <inheritdoc />
        public override ValueTask OnTurnRefusedAsync(TurnRefused notice, CancellationToken cancellationToken)
        {
            return !AuditRules.IsAudited(notice)
                ? ValueTask.CompletedTask
                : AppendAsync(notice, AuditEventKind.TurnRefused,
                    new Dictionary<string, string>(StringComparer.Ordinal) { [AuditPayloadKeys.RefusedReason] = TurnRefusalTokens.ToToken(notice.Reason) },
                    amends: null, cancellationToken);
        }

        /// <inheritdoc />
        public override ValueTask OnTurnSupersededAsync(TurnSuperseded notice, CancellationToken cancellationToken)
        {
            return AppendAsync(notice, AuditEventKind.TurnSuperseded, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.WithdrewFromTurnIndex] = notice.WithdrewFrom.ToString(CultureInfo.InvariantCulture),
                [AuditPayloadKeys.WithdrewThroughTurnIndex] = notice.WithdrewThrough.ToString(CultureInfo.InvariantCulture),
            }, amends: null, cancellationToken);
        }

        /// <inheritdoc />
        public override ValueTask OnInputModeratedAsync(InputModerated notice, CancellationToken cancellationToken)
        {
            return !AuditRules.IsAudited(notice)
                ? ValueTask.CompletedTask
                : AppendAsync(notice, AuditEventKind.PromptFlagged,
                    new Dictionary<string, string>(StringComparer.Ordinal) { [AuditPayloadKeys.ModerationCategories] = string.Join(',', notice.Categories) },
                    amends: null, cancellationToken);
        }

        /// <inheritdoc />
        public override ValueTask OnToolCalledAsync(ToolCalled notice, CancellationToken cancellationToken)
        {
            return !AuditRules.IsAudited(notice)
                ? ValueTask.CompletedTask
                : AppendAsync(notice, AuditEventKind.ToolFailed, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.ToolName] = notice.ToolName,
                    [AuditPayloadKeys.ToolCallId] = notice.CallId,
                    [AuditPayloadKeys.ToolFailureKind] = ToolFailureKinds.ToToken(notice.FailureKind ?? ToolFailureKind.Faulted),
                    [AuditPayloadKeys.ToolError] = notice.Failure ?? string.Empty,
                }, amends: null, cancellationToken);
        }

        /// <inheritdoc />
        public override ValueTask OnTurnCompletedAsync(TurnCompleted notice, CancellationToken cancellationToken)
        {
            return AppendAsync(notice, AuditEventKind.TurnCompleted, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.ReplyTextSha256] = AuditHash.OfText(notice.ReplyText).Value,
                [AuditPayloadKeys.StageBefore] = notice.StageBefore,
                [AuditPayloadKeys.StageAfter] = notice.StageAfter,
            }, amends: null, cancellationToken);
        }

        /// <inheritdoc />
        public override ValueTask OnReplyCutAsync(ReplyCut notice, CancellationToken cancellationToken)
        {
            Dictionary<string, string> payload = new(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.UtteranceUntilInterruptSha256] = AuditHash.OfText(notice.HeardText).Value,
            };

            if (notice.Played is { } played)
            {
                payload[AuditPayloadKeys.DurationUntilInterruptMs] = ((long)played.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            }

            return AppendAsync(notice, AuditEventKind.ReplyInterrupted, payload, notice.AmendsEventId, cancellationToken);
        }

        private async ValueTask AppendAsync(
            HookNotice notice, AuditEventKind kind, IReadOnlyDictionary<string, string> payload, Guid? amends, CancellationToken cancellationToken)
        {
            AuditEvent row = new()
            {
                ConversationId = notice.Scope.ConversationId!,
                EventId = notice.EventId,
                Kind = kind,
                OccurredAt = notice.Scope.OccurredAt,
                TurnIndex = notice.Scope.TurnIndex,
                AmendsEventId = amends,
                Payload = payload,
            };

            try
            {
                await sink.AppendAsync(row, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Log.AuditAppendFailed(logger, row.ConversationId, AuditEventKinds.ToToken(kind), exception);
                throw;
            }
        }
    }
}

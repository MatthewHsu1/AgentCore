using System.Globalization;
using AgentCore.Domain.Audit;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The event chain of one call. It gives every fact its identity, hands it to the observers, and
/// closes the chain once.
/// </summary>
internal sealed class CallEventChain
{
    private readonly string _callId;

    private readonly CallObserverDispatcher _observers;

    private readonly TimeProvider _time;

    private int _ended;

    internal CallEventChain(string callId, CallObserverDispatcher observers, TimeProvider time)
    {
        _callId = callId;
        _observers = observers;
        _time = time;
    }

    /// <summary>Gets whether the chain already closed. Nothing may be appended behind call.ended.</summary>
    internal bool HasEnded => Volatile.Read(ref _ended) == 1;

    /// <summary>Raises one durable fact of this call, and gives it its identity.</summary>
    internal Guid Raise(
        CallEventKind kind,
        DateTimeOffset occurredAt,
        int? turnIndex,
        Guid? amends = null,
        IReadOnlyDictionary<string, string>? payload = null)
    {
        var eventId = Guid.CreateVersion7();

        Dispatch(kind, occurredAt, turnIndex, eventId, amends, payload);

        return eventId;
    }

    /// <summary>Raises one fact that is counted and logged and stored nowhere.</summary>
    /// <param name="kind">What happened. The chain of D23 holds no row for it.</param>
    /// <param name="occurredAt">When it happened.</param>
    /// <param name="turnIndex">The turn it belongs to.</param>
    /// <param name="payload">The detail the fact carries.</param>
    internal void RaiseDiagnostic(
        CallEventKind kind,
        DateTimeOffset occurredAt,
        int? turnIndex,
        IReadOnlyDictionary<string, string>? payload = null)
        => Dispatch(kind, occurredAt, turnIndex, eventId: null, amends: null, payload);

    /// <summary>Closes the chain of this call, once.</summary>
    /// <param name="reason">Why the call ended.</param>
    /// <param name="endedAt">The moment it ended.</param>
    /// <param name="terminalStage">The stage the machine stopped in, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when this call wrote the event, and <see langword="false"/> when it already had.</returns>
    internal bool EndCall(CallEndReason reason, DateTimeOffset endedAt, string? terminalStage = null)
    {
        // The token is read first, so a value outside the closed set writes no event at all.
        var token = CallEndReasons.ToToken(reason);

        if (Interlocked.Exchange(ref _ended, 1) == 1)
        {
            return false;
        }

        Dictionary<string, string> payload = new(StringComparer.Ordinal)
        {
            [AuditPayloadKeys.EndReason] = token,
        };

        if (terminalStage is { Length: > 0 })
        {
            payload[AuditPayloadKeys.StageAfter] = terminalStage;
        }

        _ = Raise(CallEventKind.CallEnded, endedAt, turnIndex: null, payload: payload);

        return true;
    }

    /// <summary>Raises the durable facts of one finished turn, in the order they happened.</summary>
    /// <param name="turnIndex">The turn that just spoke.</param>
    /// <param name="endedAt">The moment the turn ended.</param>
    /// <param name="stageBefore">The stage the turn spoke in.</param>
    /// <param name="stageAfter">The stage the machine holds after the turn.</param>
    /// <param name="reply">The text the caller heard.</param>
    /// <param name="spokenReply">The whole reply the model produced.</param>
    /// <param name="toolFault">The message of the fault, or <see langword="null"/>.</param>
    /// <param name="interruptedAfter">The played duration, or <see langword="null"/>.</param>
    /// <returns>
    /// The identity of the <c>turn.completed</c> fact, so a barge-in that arrives after this turn
    /// already ended can name it through <see cref="CallEvent.AmendsEventId"/>.
    /// </returns>
    internal Guid WriteTurnEvents(
        int turnIndex,
        DateTimeOffset endedAt,
        string stageBefore,
        string stageAfter,
        string reply,
        string spokenReply,
        string? toolFault,
        TimeSpan? interruptedAfter)
    {
        if (toolFault is not null)
        {
            _ = Raise(
                CallEventKind.ToolFailed,
                endedAt,
                turnIndex,
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.ToolError] = toolFault,
                });
        }

        var completed = Raise(
            CallEventKind.TurnCompleted,
            endedAt,
            turnIndex,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.ReplyTextSha256] = AuditHash.OfText(spokenReply).Value,
                [AuditPayloadKeys.StageBefore] = stageBefore,
                [AuditPayloadKeys.StageAfter] = stageAfter,
            });

        if (interruptedAfter is { } played)
        {
            RaiseReplyInterrupted(turnIndex, endedAt, completed, reply, played);
        }

        return completed;
    }

    /// <summary>Raises the amendment pair's second half: the barge-in that corrects one turn.</summary>
    /// <param name="turnIndex">The turn whose reply was cut.</param>
    /// <param name="occurredAt">When the cut was recorded.</param>
    /// <param name="amendsEventId">The identity of the <c>turn.completed</c> fact it corrects.</param>
    /// <param name="heard">The text the caller actually heard.</param>
    /// <param name="played">How much of the reply played, as the relay reported it.</param>
    internal void RaiseReplyInterrupted(
        int turnIndex,
        DateTimeOffset occurredAt,
        Guid amendsEventId,
        string heard,
        TimeSpan played)
        => _ = Raise(
            CallEventKind.ReplyInterrupted,
            occurredAt,
            turnIndex,
            amends: amendsEventId,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.UtteranceUntilInterruptSha256] = AuditHash.OfText(heard).Value,
                [AuditPayloadKeys.DurationUntilInterruptMs] =
                    ((long)played.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            });

    /// <summary>Raises the moderation facts of one turn, before the turn's own events.</summary>
    /// <param name="turnIndex">The turn that just ran.</param>
    /// <param name="disposition">What the layers reported, or <see langword="null"/>.</param>
    internal void RaiseModeration(int turnIndex, TurnDisposition? disposition)
    {
        switch (disposition?.Moderation)
        {
            case ModerationOutcome.Flagged:
                _ = Raise(
                    CallEventKind.PromptFlagged,
                    _time.GetUtcNow(),
                    turnIndex,
                    payload: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [AuditPayloadKeys.ModerationCategories] = disposition.FlaggedCategories ?? string.Empty,
                    });
                break;

            case ModerationOutcome.Unavailable:
                RaiseDiagnostic(
                    CallEventKind.ModerationUnavailable,
                    _time.GetUtcNow(),
                    turnIndex,
                    payload: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [CallEventPayloadKeys.Reason] =
                            disposition.ModerationReason ?? CallSession.ModerationFaultedReason,
                    });
                break;

            case ModerationOutcome.Clean:
                RaiseDiagnostic(CallEventKind.ModerationClean, _time.GetUtcNow(), turnIndex);
                break;

            default:
                break;
        }
    }

    /// <summary>Raises the fact of one tool call that did not run to completion.</summary>
    /// <param name="turnIndex">The turn the call belongs to.</param>
    /// <param name="failure">What the function-invocation loop saw.</param>
    internal void RaiseToolFailure(int turnIndex, ToolFailure failure)
        => _ = Raise(
            CallEventKind.ToolFailed,
            _time.GetUtcNow(),
            turnIndex,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.ToolName] = failure.ToolName,
                [AuditPayloadKeys.ToolCallId] = failure.ToolCallId,
                [AuditPayloadKeys.ToolFailureKind] = ToolFailureKinds.ToToken(failure.Kind),
                [AuditPayloadKeys.ToolError] = failure.Message,
            });

    /// <summary>Raises the fact of one store 1 write the backing store refused.</summary>
    internal void RaiseDroppedTranscriptWrite(int turnIndex, Exception exception)
        => RaiseDiagnostic(
            CallEventKind.TranscriptWriteFailed,
            _time.GetUtcNow(),
            turnIndex,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CallEventPayloadKeys.Reason] = $"{exception.GetType().Name}: {exception.Message}",
            });

    /// <summary>Hands one fact to everything watching the call, and never waits for it.</summary>
    /// <param name="kind">What happened.</param>
    /// <param name="occurredAt">When it happened.</param>
    /// <param name="turnIndex">The turn it belongs to, or <see langword="null"/>.</param>
    /// <param name="eventId">The identity it took, or <see langword="null"/> when it took none.</param>
    /// <param name="amends">The identity it corrects, or <see langword="null"/>.</param>
    /// <param name="payload">The detail it carries.</param>
    private void Dispatch(
        CallEventKind kind,
        DateTimeOffset occurredAt,
        int? turnIndex,
        Guid? eventId,
        Guid? amends,
        IReadOnlyDictionary<string, string>? payload)
        => _observers.Dispatch(new CallEvent
        {
            CallId = _callId,
            Kind = kind,
            OccurredAt = occurredAt,
            EventId = eventId,
            TurnIndex = turnIndex,
            AmendsEventId = amends,
            Payload = payload ?? new Dictionary<string, string>(StringComparer.Ordinal),
        });
}

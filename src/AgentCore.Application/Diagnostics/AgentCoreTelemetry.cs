using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgentCore.Application.Diagnostics;

/// <summary>
/// The one <see cref="ActivitySource"/> and the one <see cref="Meter"/> the library owns.
/// </summary>
public static class AgentCoreTelemetry
{
    /// <summary>The name a host passes to <c>AddSource</c> to receive the spans of this library.</summary>
    public const string ActivitySourceName = "AgentCore";

    /// <summary>The name a host passes to <c>AddMeter</c> to receive the metrics of this library.</summary>
    public const string MeterName = "AgentCore";

    /// <summary>The name of the span one turn opens.</summary>
    public const string TurnActivityName = "agentcore.turn";

    /// <summary>The outcome of a turn that answered the caller.</summary>
    internal const string OutcomeCompleted = "completed";

    /// <summary>The outcome of a turn the caller spoke over.</summary>
    internal const string OutcomeInterrupted = "interrupted";

    /// <summary>The outcome of a turn that spoke the fallback of section 8.7.</summary>
    internal const string OutcomeFailed = "failed";

    /// <summary>The failure kind of section 8.7, row six: a tool failed four times in a row.</summary>
    internal const string FailureTool = "tool";

    /// <summary>The failure kind of section 8.7, last row: the run returned quietly with no text.</summary>
    internal const string FailureEmptyReply = "empty_reply";

    /// <summary>The failure kind of section 8.7, row two: the extractor returned an invalid object.</summary>
    internal const string FailureExtraction = "extraction";

    /// <summary>The moderation outcome of a turn the endpoint checked and flagged nothing in.</summary>
    internal const string ModerationClean = "clean";

    /// <summary>The moderation outcome of a turn the endpoint flagged, so the agent refused it.</summary>
    internal const string ModerationFlagged = "flagged";

    /// <summary>The moderation outcome of a turn the endpoint did not answer for.</summary>
    internal const string ModerationUnavailable = "unavailable";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter Instruments = new(MeterName);

    private static readonly Histogram<double> TurnDuration = Instruments.CreateHistogram<double>(
        "agentcore.turn.duration",
        unit: "s",
        description: "How long one turn took, from the caller's words to the spoken line.");

    private static readonly Counter<long> TurnFailures = Instruments.CreateCounter<long>(
        "agentcore.turn.failures",
        unit: "{failure}",
        description: "Turns that met one of the section 8.7 failure rows.");

    private static readonly Counter<long> AuditEvents = Instruments.CreateCounter<long>(
        "agentcore.audit.events",
        unit: "{event}",
        description: "Audit events the turn loop handed to the sink, by kind.");

    private static readonly Counter<long> ModerationVerdicts = Instruments.CreateCounter<long>(
        "agentcore.moderation.verdicts",
        unit: "{verdict}",
        description: "Turns the moderation endpoint checked, by outcome.");

    /// <summary>Opens the span of one turn.</summary>
    /// <param name="callId">The id of the call. It goes on the span, and never on a metric.</param>
    /// <param name="turnIndex">The zero-based index of the turn.</param>
    /// <param name="stageBefore">The stage the turn speaks in.</param>
    /// <returns>The span, or <see langword="null"/> when nothing listens.</returns>
    internal static Activity? StartTurn(string callId, int turnIndex, string stageBefore)
    {
        Activity? activity = Source.StartActivity(TurnActivityName, ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("gen_ai.conversation.id", callId);
        activity.SetTag("agentcore.turn.index", turnIndex);

        if (stageBefore.Length > 0)
        {
            activity.SetTag("agentcore.stage.before", stageBefore);
        }

        return activity;
    }

    /// <summary>Closes the span of one turn and records how long it took.</summary>
    /// <param name="activity">The span <see cref="StartTurn"/> opened, or <see langword="null"/>.</param>
    /// <param name="elapsed">How long the turn took.</param>
    /// <param name="outcome">One of the three closed outcome values.</param>
    /// <param name="stageAfter">The stage the machine holds after the turn.</param>
    /// <param name="failure">The section 8.7 reason, or <see langword="null"/> when the turn answered.</param>
    internal static void EndTurn(
        Activity? activity,
        TimeSpan elapsed,
        string outcome,
        string stageAfter,
        string? failure)
    {
        TurnDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("agentcore.turn.outcome", outcome));

        if (activity is null)
        {
            return;
        }

        activity.SetTag("agentcore.turn.outcome", outcome);

        if (stageAfter.Length > 0)
        {
            activity.SetTag("agentcore.stage.after", stageAfter);
        }

        if (failure is not null)
        {
            // A failed turn still speaks a line, so the call is alive. The span says the turn failed
            // and the reason names the row of section 8.7 it met.
            activity.SetStatus(ActivityStatusCode.Error, failure);
        }
    }

    /// <summary>Counts one turn that met a section 8.7 failure row.</summary>
    /// <param name="kind">One of the three closed failure kinds.</param>
    internal static void RecordFailure(string kind)
        => TurnFailures.Add(1, new KeyValuePair<string, object?>("agentcore.failure.kind", kind));

    /// <summary>Counts one audit event the turn loop handed to the sink.</summary>
    /// <param name="kindToken">The wire token of the event kind. The vocabulary is closed at seven.</param>
    internal static void RecordAuditEvent(string kindToken)
        => AuditEvents.Add(1, new KeyValuePair<string, object?>("agentcore.audit.kind", kindToken));

    /// <summary>Counts one turn the moderation endpoint was asked about.</summary>
    /// <param name="outcome">One of the three closed moderation outcomes.</param>
    internal static void RecordModeration(string outcome)
        => ModerationVerdicts.Add(1, new KeyValuePair<string, object?>("agentcore.moderation.outcome", outcome));
}

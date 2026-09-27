using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgentCore.Application.Diagnostics
{
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

        /// <summary>The failure kind of a run that faulted outside every tool, say a model endpoint that did not answer.</summary>
        internal const string FailureRun = "run";

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

        // Bucket boundaries for every agentcore.* histogram below, in seconds. The SDK default bounds are
        // millisecond-shaped ([0,5,10,…,10000]) and put every reading here in the first bucket or two, which
        // makes a percentile query meaningless (docs/probes/r2/otel/REPORT.md, audit finding F1). The values
        // are the OpenTelemetry GenAI semantic conventions' own advice, so a backend that already has GenAI
        // dashboards for gen_ai.client.operation.duration / gen_ai.server.time_to_first_token gets the same
        // resolution for these:
        // https://github.com/open-telemetry/semantic-conventions-genai/blob/main/docs/gen-ai/gen-ai-metrics.md
        private static readonly InstrumentAdvice<double> OperationDurationBuckets = new()
        {
            HistogramBucketBoundaries =
                [0.01, 0.02, 0.04, 0.08, 0.16, 0.32, 0.64, 1.28, 2.56, 5.12, 10.24, 20.48, 40.96, 81.92],
        };

        private static readonly InstrumentAdvice<double> TimeToFirstTokenBuckets = new()
        {
            HistogramBucketBoundaries =
                [0.001, 0.005, 0.01, 0.02, 0.04, 0.06, 0.08, 0.1, 0.25, 0.5, 0.75, 1.0, 2.5, 5.0, 7.5, 10.0],
        };

        private static readonly Histogram<double> TurnDuration = Instruments.CreateHistogram<double>(
            "agentcore.turn.duration",
            unit: "s",
            description: "How long one turn took, from the user's words to the reply.",
            advice: OperationDurationBuckets);

        private static readonly Histogram<double> TimeToFirstToken = Instruments.CreateHistogram<double>(
            "agentcore.turn.time_to_first_token",
            unit: "s",
            description: "From the start of one model step to its first text or tool call. One reading per step.",
            advice: TimeToFirstTokenBuckets);

        private static readonly Histogram<double> TimeToFirstSpeech = Instruments.CreateHistogram<double>(
            "agentcore.turn.time_to_first_speech",
            unit: "s",
            description: "From the user's final words to the first text of the reply handed to the output.",
            advice: TimeToFirstTokenBuckets);

        private static readonly Histogram<double> TimeToReplyEnd = Instruments.CreateHistogram<double>(
            "agentcore.turn.time_to_reply_end",
            unit: "s",
            description: "From the user's final words to the end of the last step of a reply nothing cut short.",
            advice: OperationDurationBuckets);

        private static readonly Histogram<double> BargeInLatency = Instruments.CreateHistogram<double>(
            "agentcore.barge_in.latency",
            unit: "s",
            description: "From the transport reporting a barge-in to every speech it interrupted being done.",
            advice: TimeToFirstTokenBuckets);

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
        /// <param name="conversationId">The id of the conversation. It goes on the span, and never on a metric.</param>
        /// <param name="turnIndex">The zero-based index of the turn.</param>
        /// <param name="stageBefore">The stage the turn speaks in.</param>
        /// <returns>The span, or <see langword="null"/> when nothing listens.</returns>
        internal static Activity? StartTurn(string conversationId, int turnIndex, string stageBefore)
        {
            Activity? activity = Source.StartActivity(TurnActivityName, ActivityKind.Internal);
            if (activity is null)
            {
                return null;
            }

            _ = activity.SetTag("gen_ai.conversation.id", conversationId);
            _ = activity.SetTag("agentcore.turn.index", turnIndex);

            if (stageBefore.Length > 0)
            {
                _ = activity.SetTag("agentcore.stage.before", stageBefore);
            }

            return activity;
        }

        /// <summary>Closes the span of one turn and records how long it took.</summary>
        /// <param name="activity">The span <see cref="StartTurn"/> opened, or <see langword="null"/>.</param>
        /// <param name="elapsed">How long the turn took.</param>
        /// <param name="outcome">One of the three closed outcome values.</param>
        /// <param name="stageAfter">The stage the machine holds after the turn.</param>
        /// <param name="failure">The section 8.7 reason, or <see langword="null"/> when the turn answered.</param>
        /// <param name="errorType">
        /// The OTel <c>error.type</c> value for a failed turn: an exception's full type name, or one of the
        /// closed failure-kind tokens when nothing threw. <see langword="null"/> keeps <paramref name="failure"/>
        /// itself as the span status, so a fixed, non-PII reason (such as a refused-turn message) still reads
        /// on the span. Never a value that can carry caller or model text: that belongs in the log, not here.
        /// </param>
        internal static void EndTurn(
            Activity? activity,
            TimeSpan elapsed,
            string outcome,
            string stageAfter,
            string? failure,
            string? errorType = null)
        {
            // error.type never rides the metric point, even though the SPAN carries it below: an exception's
            // type name is an open set (T61, and TurnObservabilityTests' PermittedMetricKeys), and a
            // cumulative series per distinct type would grow without bound the same way one per conversation
            // id would.
            TurnDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("agentcore.turn.outcome", outcome));

            if (activity is null)
            {
                return;
            }

            _ = activity.SetTag("agentcore.turn.outcome", outcome);

            if (stageAfter.Length > 0)
            {
                _ = activity.SetTag("agentcore.stage.after", stageAfter);
            }

            if (failure is not null)
            {
                // A failed turn still speaks a line, so the conversation is alive. The span says the turn
                // failed. Callers that pass errorType never also pass a failure built from an exception's
                // message, so the status never carries caller or model text (owner ruling: type in spans,
                // full error in logs).
                _ = activity.SetStatus(ActivityStatusCode.Error, errorType ?? failure);

                if (errorType is not null)
                {
                    _ = activity.SetTag("error.type", errorType);
                }
            }
        }

        /// <summary>Records how long the model took to say its first word of one turn.</summary>
        /// <param name="elapsed">Time since the transport handed over the user's words.</param>
        internal static void RecordTimeToFirstToken(TimeSpan elapsed)
        {
            TimeToFirstToken.Record(elapsed.TotalSeconds);
        }

        /// <summary>Records how long one turn took to hand its first fragment back to the transport.</summary>
        /// <param name="elapsed">Time since the transport handed over the user's words.</param>
        /// <remarks>
        /// This is the last point the library can see. A transport that synthesises the audio itself
        /// adds its own time to first sound on top, and no number here can reach it.
        /// </remarks>
        internal static void RecordTimeToFirstSpeech(TimeSpan elapsed)
        {
            TimeToFirstSpeech.Record(elapsed.TotalSeconds);
        }

        /// <summary>Records how long one whole reply took to hand over.</summary>
        /// <param name="elapsed">Time since the transport handed over the user's words.</param>
        /// <remarks>
        /// Only replies that ended on their own are recorded. A barge-in ends a turn before its close,
        /// so an interrupted turn contributes nothing and cannot pull the distribution down.
        /// </remarks>
        internal static void RecordTimeToReplyEnd(TimeSpan elapsed)
        {
            TimeToReplyEnd.Record(elapsed.TotalSeconds);
        }

        /// <summary>Records how long a barge-in took to take effect.</summary>
        /// <param name="elapsed">Time from the transport reporting the barge-in to the queued reply being dropped.</param>
        internal static void RecordBargeInLatency(TimeSpan elapsed)
        {
            BargeInLatency.Record(elapsed.TotalSeconds);
        }

        /// <summary>Counts one turn that met a section 8.7 failure row.</summary>
        /// <param name="kind">One of the four closed failure kinds.</param>
        internal static void RecordFailure(string kind)
        {
            TurnFailures.Add(1, new KeyValuePair<string, object?>("agentcore.failure.kind", kind));
        }

        /// <summary>Counts one audit event the turn loop handed to the sink.</summary>
        /// <param name="kindToken">The wire token of the event kind. The vocabulary is closed at seven.</param>
        internal static void RecordAuditEvent(string kindToken)
        {
            AuditEvents.Add(1, new KeyValuePair<string, object?>("agentcore.audit.kind", kindToken));
        }

        /// <summary>Counts one turn the moderation endpoint was asked about.</summary>
        /// <param name="outcome">One of the three closed moderation outcomes.</param>
        internal static void RecordModeration(string outcome)
        {
            ModerationVerdicts.Add(1, new KeyValuePair<string, object?>("agentcore.moderation.outcome", outcome));
        }
    }
}

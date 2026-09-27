using System.Collections.ObjectModel;
using System.Diagnostics.Metrics;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;
using Xunit;

namespace AgentCore.Application.Tests.Diagnostics
{
    /// <summary>
    /// The three counters of section 8.6, incremented from a fact instead of from the turn loop.
    /// </summary>
    public sealed class TelemetryConversationObserverTests
    {
        private const string FailureInstrument = "agentcore.turn.failures";
        private const string ModerationInstrument = "agentcore.moderation.verdicts";
        private const string AuditInstrument = "agentcore.audit.events";

        private const string FailureKey = "agentcore.failure.kind";
        private const string ModerationKey = "agentcore.moderation.outcome";
        private const string AuditKey = "agentcore.audit.kind";

        /// <summary>Each failure kind, beside the value section 8.7 gives its row.</summary>
        public static TheoryData<ConversationEventKind, string> Failures =>
            new()
            {
                { ConversationEventKind.ToolFailed, "tool" },
                { ConversationEventKind.EmptyReply, "empty_reply" },
                { ConversationEventKind.ExtractionFailed, "extraction" },
                { ConversationEventKind.RunFaulted, "run" },
            };

        /// <summary>Each moderation verdict, beside the value an operator alerts on.</summary>
        public static TheoryData<ConversationEventKind, string> Verdicts =>
            new()
            {
                { ConversationEventKind.PromptFlagged, "flagged" },
                { ConversationEventKind.ModerationClean, "clean" },
                { ConversationEventKind.ModerationUnavailable, "unavailable" },
            };

        /// <summary>Each stored kind, beside the wire token the chain of D23 hashes.</summary>
        public static TheoryData<ConversationEventKind, string> StoredKinds =>
            new()
            {
                { ConversationEventKind.ConversationStarted, "conversation.started" },
                { ConversationEventKind.PromptFlagged, "prompt.flagged" },
                { ConversationEventKind.ToolFailed, "tool.failed" },
                { ConversationEventKind.TurnCompleted, "turn.completed" },
                { ConversationEventKind.ReplyInterrupted, "reply.interrupted" },
                { ConversationEventKind.ConversationEnded, "conversation.ended" },
            };

        [Theory]
        [MemberData(nameof(Failures))]
        public async Task AFailureRow_IsCountedUnderItsOwnKind(ConversationEventKind kind, string expected)
        {
            List<Reading> measured = await MeasureAsync(kind);

            Assert.Contains(new Reading(FailureInstrument, FailureKey, expected), measured);
        }

        [Theory]
        [MemberData(nameof(Verdicts))]
        public async Task AModerationVerdict_IsCountedUnderItsOwnOutcome(ConversationEventKind kind, string expected)
        {
            List<Reading> measured = await MeasureAsync(kind);

            Assert.Contains(new Reading(ModerationInstrument, ModerationKey, expected), measured);
        }

        [Theory]
        [MemberData(nameof(StoredKinds))]
        public async Task AStoredKind_IsCountedUnderTheTokenTheChainWrites(ConversationEventKind kind, string expected)
        {
            List<Reading> measured = await MeasureAsync(kind);

            Assert.Contains(new Reading(AuditInstrument, AuditKey, expected), measured);
        }

        [Fact]
        public async Task NoEvent_IsRefused()
        {
            _ = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                        await new TelemetryConversationObserver().OnConversationEventAsync(null!, TestContext.Current.CancellationToken));
        }

        // A turn whose tool fails four times raises four per-call facts, each with a tool call id, and one
        // turn-level fact with none. agentcore.turn.failures promises one count per failed turn, so only the
        // turn-level fact moves it (audit finding E5, docs/probes/r2/audit/AUDIT.md).
        [Fact]
        public async Task APerCallToolFailure_MovesNothing_OnlyTheTurnLevelFactDoes()
        {
            List<Reading> perCall = await MeasureAsync(
                ConversationEventKind.ToolFailed,
                new Dictionary<string, string>(StringComparer.Ordinal) { [AuditPayloadKeys.ToolCallId] = "call-1" });

            Assert.DoesNotContain(new Reading(FailureInstrument, FailureKey, "tool"), perCall);

            List<Reading> turnLevel = await MeasureAsync(ConversationEventKind.ToolFailed);

            Assert.Contains(new Reading(FailureInstrument, FailureKey, "tool"), turnLevel);
        }

        /// <summary>Hands one fact to the observer and reads back what the meter saw.</summary>
        /// <param name="kind">What happened.</param>
        /// <param name="payload">The fact's payload, or empty for a kind that carries none in this suite.</param>
        /// <returns>Every attribute of every measurement, as instrument, key, and value.</returns>
        private static async Task<List<Reading>> MeasureAsync(
            ConversationEventKind kind, IReadOnlyDictionary<string, string>? payload = null)
        {
            List<Reading> readings = [];
            using MeterListener listener = new();

            listener.InstrumentPublished = (instrument, active) =>
            {
                if (string.Equals(instrument.Meter.Name, AgentCoreTelemetry.MeterName, StringComparison.Ordinal))
                {
                    active.EnableMeasurementEvents(instrument);
                }
            };

            listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                KeyValuePair<string, object?>[] copy = tags.ToArray();
                lock (readings)
                {
                    readings.AddRange(copy.Select(tag =>
                        new Reading(instrument.Name, tag.Key, tag.Value?.ToString() ?? string.Empty)));
                }
            });

            listener.Start();

            await new TelemetryConversationObserver().OnConversationEventAsync(
                new ConversationEvent
                {
                    ConversationId = "conversation-1",
                    Kind = kind,
                    OccurredAt = DateTimeOffset.UnixEpoch,
                    EventId = Guid.CreateVersion7(),
                    TurnIndex = 0,
                    Payload = payload ?? ReadOnlyDictionary<string, string>.Empty,
                },
                TestContext.Current.CancellationToken);

            listener.Dispose();

            lock (readings)
            {
                return [.. readings];
            }
        }

        /// <summary>One attribute of one measurement.</summary>
        /// <param name="Instrument">The instrument that took it.</param>
        /// <param name="Key">The attribute key.</param>
        /// <param name="Value">The attribute value.</param>
        private sealed record Reading(string Instrument, string Key, string Value);
    }
}

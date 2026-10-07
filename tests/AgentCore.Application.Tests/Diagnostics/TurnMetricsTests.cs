using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Tests.Runtime;
using System.Diagnostics.Metrics;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Diagnostics.TurnObservabilityHarness;

namespace AgentCore.Application.Tests.Diagnostics
{
    /// <summary>
    /// The metrics a turn reports, and the attributes they may carry.
    /// </summary>
    public sealed class TurnMetricsTests
    {
        /// <summary>The only attribute keys any metric of this library may carry.</summary>
        private static readonly string[] PermittedMetricKeys =
        [
            "agentcore.turn.outcome",
            "agentcore.failure.kind",
            "agentcore.audit.kind",

            // Three values: clean, flagged, unavailable. Three series for each replica, against the
            // 10,000 ceiling. No conversation id rides on it.
            "agentcore.moderation.outcome",
        ];

        [Fact]
        public async Task NoMetricAttribute_EverCarriesTheConversationId()
        {
            // A value nothing else in the process can produce, so a hit is this conversation and not another.
            string conversationId = "conversation-" + Guid.NewGuid().ToString("N");

            List<KeyValuePair<string, object?>> tags = await MeasureAsync(conversationId);

            // The .NET default is cumulative temporality, so one conversation id on a metric attribute is one
            // permanent series. A day of conversations would then be a day of permanent series, and the free
            // tier binds at 10,000.
            Assert.DoesNotContain(tags, tag => string.Equals(tag.Value as string, conversationId, StringComparison.Ordinal));
        }

        [Fact]
        public async Task EveryMetricAttribute_ComesFromAClosedSet()
        {
            List<KeyValuePair<string, object?>> tags = await MeasureAsync("conversation-" + Guid.NewGuid().ToString("N"));

            // The listener really saw the instruments of this library.
            Assert.NotEmpty(tags);

            // This half catches the next attribute somebody adds. Every key
            // below takes a handful of values the library writes itself. A key that is not on this list
            // has not been costed, so the build refuses it.
            Assert.All(tags, tag => Assert.Contains(tag.Key, PermittedMetricKeys, StringComparer.Ordinal));
        }

        [Fact]
        public async Task ATurn_RecordsItsDurationOnceWithTheOutcome()
        {
            List<Measurement<double>> durations = [];
            using MeterListener listener = new();
            listener.InstrumentPublished = (instrument, active) =>
            {
                if (string.Equals(instrument.Meter.Name, AgentCoreTelemetry.MeterName, StringComparison.Ordinal)
                    && string.Equals(instrument.Name, "agentcore.turn.duration", StringComparison.Ordinal))
                {
                    active.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            {
                lock (durations)
                {
                    durations.Add(new Measurement<double>(value, tags));
                }
            });
            listener.Start();

            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            TestTimeProvider clock = new();
            ConversationSession session = Build(PolicyYaml, reply, fill, timeProvider: clock).Create();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
            listener.Dispose();

            // Other tests may run beside this one, so the assertion reads what this turn produced rather
            // than the whole list.
            Assert.Contains(durations, sample => sample.Tags.ToArray().Any(tag =>
                string.Equals(tag.Key, "agentcore.turn.outcome", StringComparison.Ordinal)
                && string.Equals(tag.Value as string, "completed", StringComparison.Ordinal)));
        }

        /// <summary>Runs three turns of one conversation and collects every metric attribute the library wrote.</summary>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <returns>Every attribute of every measurement, flattened.</returns>
        private static async Task<List<KeyValuePair<string, object?>>> MeasureAsync(string conversationId)
        {
            List<KeyValuePair<string, object?>> tags = [];
            using MeterListener listener = new();

            listener.InstrumentPublished = (instrument, active) =>
            {
                if (string.Equals(instrument.Meter.Name, AgentCoreTelemetry.MeterName, StringComparison.Ordinal))
                {
                    active.EnableMeasurementEvents(instrument);
                }
            };

            listener.SetMeasurementEventCallback<double>((_, _, measured, _) => Collect(tags, measured));
            listener.SetMeasurementEventCallback<long>((_, _, measured, _) => Collect(tags, measured));
            listener.Start();

            using SequencedChatClient reply = new("hello there.", "   ", "goodbye.");
            using SequencedChatClient fill = new(StayingNull, StayingNull, /*lang=json,strict*/ """{ "callerSaidGoodbye": true }""");
            ConversationSession session = Build(PolicyYaml, reply, fill, auditSink: new InMemoryAuditSink())
                .Create(conversationId);

            CancellationToken token = TestContext.Current.CancellationToken;
            _ = await session.RunTurnAsync("hi", token);
            _ = await session.RunTurnAsync("still there?", token);
            _ = await session.RunTurnAsync("goodbye", token);
            await session.FlushNoticesAsync();

            listener.Dispose();
            return tags;
        }

        /// <summary>Adds the attributes of one measurement to the list.</summary>
        private static void Collect(
            List<KeyValuePair<string, object?>> tags,
            ReadOnlySpan<KeyValuePair<string, object?>> measured)
        {
            KeyValuePair<string, object?>[] copy = measured.ToArray();
            lock (tags)
            {
                tags.AddRange(copy);
            }
        }
    }
}

using System.Diagnostics.Metrics;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.BuiltIn;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Domain.Audit;
using Xunit;

namespace AgentCore.Application.Tests.Hooks
{
    [Collection(TelemetryHookSuite.Name)]
    public sealed class TelemetryHookTests
    {
        private const string FailureInstrument = "agentcore.turn.failures";
        private const string FailureKey = "agentcore.failure.kind";
        private const string ModerationInstrument = "agentcore.moderation.verdicts";
        private const string ModerationKey = "agentcore.moderation.outcome";
        private const string AuditInstrument = "agentcore.audit.events";
        private const string AuditKey = "agentcore.audit.kind";

        private static readonly HookScope Scope = new("c1", "main", 0, "", Guid.CreateVersion7(), 1, DateTimeOffset.UnixEpoch);

        private sealed record Reading(string Instrument, string Key, string Value);

        public static TheoryData<TurnCompleted, string> FailedTurns => new()
        {
            { Turn(TurnOutcome.Empty, inTool: false), "empty_reply" },
            { Turn(TurnOutcome.Fallback, inTool: true), "tool" },
            { Turn(TurnOutcome.Fallback, inTool: false), "run" },
            { Turn(TurnOutcome.Faulted, inTool: false), "run" },
        };

        [Theory]
        [MemberData(nameof(FailedTurns))]
        public async Task AFailedTurnIsCountedUnderItsKind(TurnCompleted completed, string kind)
        {
            List<Reading> measured = await MeasureAsync(hook => hook.OnTurnCompletedAsync(completed, TestContext.Current.CancellationToken));

            Assert.Contains(new Reading(FailureInstrument, FailureKey, kind), measured);
            Assert.Contains(new Reading(AuditInstrument, AuditKey, "turn.completed"), measured);
        }

        [Fact]
        public async Task AnAnsweredTurnIsNoFailure()
        {
            List<Reading> measured = await MeasureAsync(hook => hook.OnTurnCompletedAsync(Turn(TurnOutcome.Answered, inTool: false), TestContext.Current.CancellationToken));

            Assert.DoesNotContain(measured, reading => reading.Instrument == FailureInstrument);
        }

        [Theory]
        [InlineData(InputVerdict.Clean, "clean")]
        [InlineData(InputVerdict.Flagged, "flagged")]
        [InlineData(InputVerdict.Unavailable, "unavailable")]
        public async Task AModerationVerdictIsCountedUnderItsOutcome(InputVerdict verdict, string outcome)
        {
            InputModerated moderated = new(Scope, verdict, verdict == InputVerdict.Flagged ? ["violence"] : [], null);

            List<Reading> measured = await MeasureAsync(hook => hook.OnInputModeratedAsync(moderated, TestContext.Current.CancellationToken));

            Assert.Contains(new Reading(ModerationInstrument, ModerationKey, outcome), measured);
        }

        [Fact]
        public async Task AnExtractionFaultIsCountedAsAnExtractionFailure()
        {
            List<Reading> measured = await MeasureAsync(hook => hook.OnFaultAsync(new Fault(Scope, FaultKind.ExtractionFailed, "bad object", null), TestContext.Current.CancellationToken));

            Assert.Contains(new Reading(FailureInstrument, FailureKey, "extraction"), measured);
        }

        [Fact]
        public async Task EveryAuditedFactIsCountedUnderItsToken()
        {
            List<Reading> measured = await MeasureAsync(async hook =>
            {
                CancellationToken ct = TestContext.Current.CancellationToken;
                await hook.OnConversationStartedAsync(new ConversationStarted(Scope, ConversationOrigin.New), ct);
                await hook.OnConversationEndedAsync(new ConversationEnded(Scope, ConversationEndReason.CallerHungUp, null, null), ct);
                await hook.OnTurnRefusedAsync(new TurnRefused(Scope, TurnRefusal.Busy, AfterEnd: false), ct);
                await hook.OnTurnSupersededAsync(new TurnSuperseded(Scope, 1, 2), ct);
                await hook.OnReplyCutAsync(new ReplyCut(Scope, "hi", null, Guid.CreateVersion7()), ct);
                await hook.OnToolCalledAsync(new ToolCalled(Scope, "t", "c", TimeSpan.Zero, ToolOutcome.Undeclared, false, ToolFailureKind.Undeclared, "x"), ct);
                await hook.OnInputModeratedAsync(new InputModerated(Scope, InputVerdict.Flagged, ["violence"], null), ct);
            });

            string[] tokens = ["conversation.started", "conversation.ended", "turn.refused", "turn.superseded", "reply.interrupted", "tool.failed", "prompt.flagged"];
            Assert.All(tokens, token => Assert.Contains(new Reading(AuditInstrument, AuditKey, token), measured));
        }

        [Fact]
        public async Task AFactTheChainWritesNoRowForIsNotCounted()
        {
            List<Reading> measured = await MeasureAsync(async hook =>
            {
                CancellationToken ct = TestContext.Current.CancellationToken;
                await hook.OnTurnRefusedAsync(new TurnRefused(Scope, TurnRefusal.Terminal, AfterEnd: true), ct);
                await hook.OnToolCalledAsync(new ToolCalled(Scope, "t", "c", TimeSpan.Zero, ToolOutcome.Ok, false, null, null), ct);
                await hook.OnInputModeratedAsync(new InputModerated(Scope, InputVerdict.Clean, [], null), ct);
            });

            Assert.DoesNotContain(measured, reading => reading.Instrument == AuditInstrument);
        }

        [Theory]
        [InlineData(LatencyMetric.TimeToFirstToken, "agentcore.turn.time_to_first_token")]
        [InlineData(LatencyMetric.TimeToFirstSpeech, "agentcore.turn.time_to_first_speech")]
        [InlineData(LatencyMetric.TimeToReplyEnd, "agentcore.turn.time_to_reply_end")]
        [InlineData(LatencyMetric.BargeIn, "agentcore.barge_in.latency")]
        public async Task EachLatencyIsRecordedOnItsSignedOffHistogram(LatencyMetric metric, string instrument)
        {
            List<(string Instrument, double Value)> readings = [];
            using MeterListener listener = new();
            listener.InstrumentPublished = (published, active) =>
            {
                if (string.Equals(published.Meter.Name, AgentCoreTelemetry.MeterName, StringComparison.Ordinal))
                {
                    active.EnableMeasurementEvents(published);
                }
            };
            listener.SetMeasurementEventCallback<double>((published, value, _, _) =>
            {
                lock (readings)
                {
                    readings.Add((published.Name, value));
                }
            });
            listener.Start();

            await new TelemetryHook().OnTurnLatencyAsync(new TurnLatency(Scope, metric, TimeSpan.FromMilliseconds(250)), TestContext.Current.CancellationToken);

            Assert.Contains((instrument, 0.25), readings);
        }

        private static TurnCompleted Turn(TurnOutcome outcome, bool inTool) =>
            new(Scope, outcome, "hi", "x", "", "", TimeSpan.Zero, outcome == TurnOutcome.Answered ? null : "why", inTool, null);

        private static async Task<List<Reading>> MeasureAsync(Func<AgentHook, ValueTask> deliver)
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
                    readings.AddRange(copy.Select(tag => new Reading(instrument.Name, tag.Key, tag.Value?.ToString() ?? string.Empty)));
                }
            });
            listener.Start();

            await deliver(new TelemetryHook());

            lock (readings)
            {
                return [.. readings];
            }
        }
    }
}

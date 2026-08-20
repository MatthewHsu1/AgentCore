using System.Diagnostics.Metrics;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Runtime;
using Xunit;

namespace AgentCore.Application.Tests.Diagnostics;

/// <summary>
/// The three counters of section 8.6, incremented from a fact instead of from the turn loop.
/// </summary>
/// <remarks>
/// <para>
/// Nothing about the numbers changed when the call sites moved behind the hook, so these tests pin the
/// instrument, the attribute key, and the value each kind writes. T61 is why the values are closed and
/// why no call id appears among them: a cumulative series lives forever once it is created, and the
/// Grafana Cloud free tier binds at 10,000.
/// </para>
/// <para>
/// The meter is a process-wide singleton, so another test running beside this one measures on the same
/// instruments. Every assertion here is therefore about what the observer DID write, and the negative
/// side — which kinds are counted as audit events at all — is pinned on the mapping itself, in
/// <c>CallEventKindsTests</c>, where nothing else can add to the reading.
/// </para>
/// </remarks>
public sealed class TelemetryCallObserverTests
{
    private const string FailureInstrument = "agentcore.turn.failures";
    private const string ModerationInstrument = "agentcore.moderation.verdicts";
    private const string AuditInstrument = "agentcore.audit.events";

    private const string FailureKey = "agentcore.failure.kind";
    private const string ModerationKey = "agentcore.moderation.outcome";
    private const string AuditKey = "agentcore.audit.kind";

    /// <summary>Each failure kind, beside the value section 8.7 gives its row.</summary>
    public static TheoryData<CallEventKind, string> Failures =>
        new()
        {
            { CallEventKind.ToolFailed, "tool" },
            { CallEventKind.EmptyReply, "empty_reply" },
            { CallEventKind.ExtractionFailed, "extraction" },
        };

    /// <summary>Each moderation verdict, beside the value an operator alerts on.</summary>
    public static TheoryData<CallEventKind, string> Verdicts =>
        new()
        {
            { CallEventKind.PromptFlagged, "flagged" },
            { CallEventKind.ModerationClean, "clean" },
            { CallEventKind.ModerationUnavailable, "unavailable" },
        };

    /// <summary>Each stored kind, beside the wire token the chain of D23 hashes.</summary>
    public static TheoryData<CallEventKind, string> StoredKinds =>
        new()
        {
            { CallEventKind.CallStarted, "call.started" },
            { CallEventKind.PromptFlagged, "prompt.flagged" },
            { CallEventKind.ToolFailed, "tool.failed" },
            { CallEventKind.TurnCompleted, "turn.completed" },
            { CallEventKind.ReplyInterrupted, "reply.interrupted" },
            { CallEventKind.CallEnded, "call.ended" },
        };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailureRow_IsCountedUnderItsOwnKind(CallEventKind kind, string expected)
    {
        var measured = await MeasureAsync(kind);

        Assert.Contains(new Reading(FailureInstrument, FailureKey, expected), measured);
    }

    [Theory]
    [MemberData(nameof(Verdicts))]
    public async Task AModerationVerdict_IsCountedUnderItsOwnOutcome(CallEventKind kind, string expected)
    {
        var measured = await MeasureAsync(kind);

        Assert.Contains(new Reading(ModerationInstrument, ModerationKey, expected), measured);
    }

    [Theory]
    [MemberData(nameof(StoredKinds))]
    public async Task AStoredKind_IsCountedUnderTheTokenTheChainWrites(CallEventKind kind, string expected)
    {
        var measured = await MeasureAsync(kind);

        Assert.Contains(new Reading(AuditInstrument, AuditKey, expected), measured);
    }

    [Fact]
    public async Task NoEvent_IsRefused()
        => await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await new TelemetryCallObserver().OnCallEventAsync(null!, TestContext.Current.CancellationToken));

    /// <summary>Hands one fact to the observer and reads back what the meter saw.</summary>
    /// <param name="kind">What happened.</param>
    /// <returns>Every attribute of every measurement, as instrument, key, and value.</returns>
    private static async Task<List<Reading>> MeasureAsync(CallEventKind kind)
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
            var copy = tags.ToArray();
            lock (readings)
            {
                readings.AddRange(copy.Select(tag =>
                    new Reading(instrument.Name, tag.Key, tag.Value?.ToString() ?? string.Empty)));
            }
        });

        listener.Start();

        await new TelemetryCallObserver().OnCallEventAsync(
            new CallEvent
            {
                CallId = "call-1",
                Kind = kind,
                OccurredAt = DateTimeOffset.UnixEpoch,
                Ordinal = 0,
                TurnIndex = 0,
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

using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using AgentCore.Application.Diagnostics;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay;

/// <summary>
/// What one inbound frame costs in observability. Section 3.1 and T61: never once for each frame.
/// </summary>
/// <remarks>
/// <para>
/// Section 3.1 states the rule as "never log or meter once for each audio frame". There is no audio
/// anywhere in this solution — item 6c holds "no audio buffer, codec, sample rate, or playback
/// clock" by construction, and the Conversation Relay carries text frames over a WebSocket instead —
/// so the frame is what that rule is actually about. It is the thing that arrives many times a
/// second on a live call, and it is the thing that must not cost a log line or a metric point.
/// </para>
/// <para>
/// The proof is a comparison rather than a threshold, because a threshold would have to guess how
/// many lines a call is allowed. One connection is driven twice, once with a few frames and once
/// with ten times as many, and the two runs must leave exactly the same number of log records and
/// exactly the same number of metric measurements behind. A signal that fires once for each frame
/// grows with the frame count and fails here; a signal that fires once for the call or once for the
/// turn does not, because both runs run one call and no turn at all.
/// </para>
/// <para>
/// The frames are interim transcripts — <c>prompt</c> with <c>last: false</c> — which T70 drops
/// outright: <c>DispatchAsync</c> matches them with an empty <c>case RelayFrame.Prompt:</c> and the
/// turn starts on the final one instead. Fifty of them must therefore do exactly as much work as
/// five of them, namely none, so anything this test does see growing is observability and nothing
/// else.
/// </para>
/// <para>
/// Every test here runs offline against a fake model and a fake relay. There is no Telnyx account,
/// no network call, and no API key anywhere in this file.
/// </para>
/// </remarks>
[Collection(TelnyxRelayFrameCostSuite.Name)]
public sealed class TelnyxRelayFrameCostTests
{
    /// <summary>The small run.</summary>
    private const int FewFrames = 5;

    /// <summary>The large run. Ten times the frames, and it must cost the same.</summary>
    private const int ManyFrames = 50;

    [Fact(Timeout = 60_000)]
    public async Task TenTimesTheInboundFrames_CostNoMoreLogRecordsAndNoMoreMetricMeasurements()
    {
        var few = await MeasureAsync(FewFrames, "call-frame-cost-few");
        var many = await MeasureAsync(ManyFrames, "call-frame-cost-many");

        // The listener really found this library's meter. Without this the measurement assertion
        // below could pass on two empty subscriptions and prove nothing at all.
        Assert.NotEqual(0, few.Instruments);
        Assert.Equal(few.Instruments, many.Instruments);

        // The provider really was in the host's logging pipeline, for the same reason.
        Assert.NotEqual(0, few.Records);

        // Section 3.1, both halves of it. Ten times the frames, and not one line and not one
        // measurement more. The counts are not zero — a call still logs what a call costs, and the
        // one dtmf frame each run ends with is one line in both — but nothing here is charged to a
        // frame, so nothing here moves when the frame count does.
        Assert.Equal(few.Records, many.Records);
        Assert.Equal(few.Measurements, many.Measurements);
    }

    /// <summary>Drives one connection over <paramref name="frames"/> interim prompts and counts what it cost.</summary>
    /// <param name="frames">How many <c>prompt</c> frames with <c>last: false</c> to send.</param>
    /// <param name="callId">The <c>callSessionId</c> of this run's setup frame.</param>
    /// <returns>What the run wrote, metered, and subscribed to.</returns>
    private static async Task<FrameCost> MeasureAsync(int frames, string callId)
    {
        // The instruments are static fields of AgentCoreTelemetry, and a const never touches a type
        // initializer, so nothing this file references would otherwise create the meter. Forcing
        // the initializer before the listener starts is what makes the instrument count a real
        // check that the listener found this library rather than an empty subscription.
        RuntimeHelpers.RunClassConstructor(typeof(AgentCoreTelemetry).TypeHandle);

        Lock gate = new();
        var measurements = 0;
        var instruments = 0;

        void RecordMeasurement()
        {
            lock (gate)
            {
                measurements++;
            }
        }

        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, active) =>
        {
            // Meter name and nothing else. T61 forbids a call id on a metric attribute, so there is
            // deliberately no tag on any of these instruments that could narrow this to one call —
            // which is why this collection runs alone, below.
            if (!string.Equals(instrument.Meter.Name, AgentCoreTelemetry.MeterName, StringComparison.Ordinal))
            {
                return;
            }

            lock (gate)
            {
                instruments++;
            }

            active.EnableMeasurementEvents(instrument);
        };

        // Every numeric type an instrument may carry, not only the double of the turn histogram and
        // the long of the three counters. A per-frame instrument somebody adds later is counted
        // here whatever its type, rather than slipping past an incomplete set of callbacks.
        listener.SetMeasurementEventCallback<byte>((_, _, _, _) => RecordMeasurement());
        listener.SetMeasurementEventCallback<short>((_, _, _, _) => RecordMeasurement());
        listener.SetMeasurementEventCallback<int>((_, _, _, _) => RecordMeasurement());
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => RecordMeasurement());
        listener.SetMeasurementEventCallback<float>((_, _, _, _) => RecordMeasurement());
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => RecordMeasurement());
        listener.SetMeasurementEventCallback<decimal>((_, _, _, _) => RecordMeasurement());
        listener.Start();

        RecordCountingLoggerProvider records = new();

        // An interim prompt leaves no trace at all — that is the point of it — so nothing about it
        // can be waited on. A dtmf frame behind the last of them can be: the socket delivers
        // messages in order and the read loop dispatches them in order, so the line this frame
        // writes is proof that every prompt ahead of it has already been read and dropped. It costs
        // one log record, the same one, in both runs.
        EventObservedLoggerProvider sentinel = new("DtmfReceived");

        using SequencedChatClient reply = new("hello");
        var host = await TelnyxRelayHost.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            logging: logging =>
            {
                logging.AddProvider(records);
                logging.AddProvider(sentinel);
            });

        try
        {
            await using var relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup(callSessionId: callId));
            await host.WaitForSessionAsync(callId);

            for (var frame = 0; frame < frames; frame++)
            {
                // The same words every time, so the two runs differ in frame count and in nothing
                // else.
                await relay.SendAsync(RelayFrames.Prompt("hello", last: false));
            }

            await relay.SendAsync(RelayFrames.Dtmf("1"));

            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token, TestContext.Current.CancellationToken);

            try
            {
                await sentinel.Observed.WaitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail($"the connection never read past the {frames} interim prompts within twenty seconds.");
            }

            // Counted here, with the call still up, rather than after the socket and the host are
            // torn down. Teardown is once for a call and never once for a frame, so it is not what
            // this test is about, and it is the one part of a run that is not reproducible line for
            // line: FakeRelayClient.DisposeAsync cancels its own pending receive, which — as that
            // class documents — leaves the socket Aborted rather than closed, so the connection
            // sometimes reaches CallDroppedWithNoCloseFrame before the host stops and sometimes does
            // not, and the host's own shutdown lines interleave with Kestrel's either way. Every
            // line up to and including the one the sentinel just waited for is fixed.
            listener.Dispose();
            return new FrameCost(records.Count, measurements, instruments);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>What one run of <see cref="MeasureAsync"/> cost.</summary>
    /// <param name="Records">How many log records the host wrote, at every level and category.</param>
    /// <param name="Measurements">How many measurements the instruments of this library took.</param>
    /// <param name="Instruments">How many instruments of this library the listener subscribed to.</param>
    private readonly record struct FrameCost(int Records, int Measurements, int Instruments);
}

/// <summary>
/// The collection <see cref="TelnyxRelayFrameCostTests"/> runs in, alone.
/// </summary>
/// <remarks>
/// A <see cref="MeterListener"/> subscribes to a meter for the whole process, and T61 is the reason
/// it cannot narrow that to one call: no instrument of this library carries a call id, a session id,
/// or a turn id, because one such attribute is one permanent series. A turn running in another test
/// class at the same moment would therefore land on this listener and be counted as this call's
/// cost. Parallelisation is switched off for this collection so that never happens; xUnit runs a
/// collection marked this way apart from every other collection in the assembly.
/// </remarks>
[CollectionDefinition(TelnyxRelayFrameCostSuite.Name, DisableParallelization = true)]
public sealed class TelnyxRelayFrameCostSuite
{
    /// <summary>The name both the definition and the test class name it by.</summary>
    public const string Name = "telnyx relay frame cost";
}

/// <summary>
/// Counts every log record the host wrote, whatever its category, level, or event id.
/// </summary>
/// <remarks>
/// <see cref="EventObservedLoggerProvider"/> counts the lines of one named event, which is what a
/// test proving that a particular line fired needs. Section 3.1 is the opposite question — nothing
/// at all may fire once for each frame, whether or not anyone thought to name it — so this one
/// counts the whole pipeline instead and cares about no name.
/// </remarks>
internal sealed class RecordCountingLoggerProvider : ILoggerProvider
{
    private int _count;

    /// <summary>Gets how many records have reached this provider so far.</summary>
    public int Count => Volatile.Read(ref _count);

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
        // Nothing to release.
    }

    private sealed class Logger(RecordCountingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Interlocked.Increment(ref owner._count);
    }
}

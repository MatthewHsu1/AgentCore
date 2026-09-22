using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.Conversation;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.Domain.Knowledge;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Conversation
{
    /// <summary>
    /// The four latency numbers a voice turn reports, taken off a clock the test owns.
    /// </summary>
    /// <remarks>
    /// Every expected value here is arithmetic on the steps the test makes the clock take, so the
    /// numbers come from the schedule the test wrote and not from the code under test.
    /// </remarks>
    [Collection(ConversationTurnLatencySuite.Name)]
    public sealed class ConversationTurnLatencyTests
    {
        private const string FirstToken = "agentcore.turn.time_to_first_token";
        private const string FirstSpeech = "agentcore.turn.time_to_first_speech";
        private const string ReplyEnd = "agentcore.turn.time_to_reply_end";

        [Fact(Timeout = 30_000)]
        public async Task AReplyThatEndsOnItsOwn_ReportsOneReadingOnEachOfTheThreeTurnClocks()
        {
            // The scripted conversation yields two fragments. Each clock still reports once: they
            // measure the first of a thing, not every one of them.
            FakeTimeProvider clock = new(DateTimeOffset.UnixEpoch);
            using LatencyReadings readings = new();

            ScriptedConversationPort session = new();
            FakeConversationOutput output = new();
            ConversationTurnArbiter arbiter = NewArbiter(session, output);

            Task turn = arbiter.StartTurnAsync("one", new ConversationTurnClock(clock));
            clock.Advance(TimeSpan.FromMilliseconds(200));
            session.ReleaseTurn();
            await turn;
            await arbiter.CurrentTurn;

            Assert.Equal([0.200], readings.Of(FirstToken));
            Assert.Equal([0.200], readings.Of(FirstSpeech));
            Assert.Equal([0.200], readings.Of(ReplyEnd));
        }

        [Fact(Timeout = 30_000)]
        public async Task AHeldPrompt_IsMeasuredFromWhenItArrivedAndNotFromWhenItRan()
        {
            // The whole reason the clock is built on the read loop and rides with the prompt. The
            // caller's second sentence arrives 50 ms in and waits behind the turn in flight. Both
            // turns then run at 120 ms on this clock, so the first answers in 120 ms and the second
            // in 70 ms. A clock started when a turn starts running would report 0 ms for the second
            // and hide the wait the caller actually sat through.
            FakeTimeProvider clock = new(DateTimeOffset.UnixEpoch);
            using LatencyReadings readings = new();

            ScriptedConversationPort session = new();
            FakeConversationOutput output = new();
            ConversationTurnArbiter arbiter = NewArbiter(session, output);

            Task first = arbiter.StartTurnAsync("one", new ConversationTurnClock(clock));

            clock.Advance(TimeSpan.FromMilliseconds(50));
            await arbiter.StartTurnAsync("two", new ConversationTurnClock(clock));

            clock.Advance(TimeSpan.FromMilliseconds(70));
            session.ReleaseTurn();
            await first;
            await arbiter.CurrentTurn;

            Assert.Equal(["one", "two"], session.TurnsRun);
            Assert.Equal([0.120, 0.070], readings.Of(FirstToken));
        }

        [Fact(Timeout = 30_000)]
        public async Task ADroppedPrompt_ReportsNothing()
        {
            // A third sentence during one turn is dropped rather than queued, and its clock goes with
            // it. Two turns ran, so two readings — never three.
            FakeTimeProvider clock = new(DateTimeOffset.UnixEpoch);
            using LatencyReadings readings = new();

            ScriptedConversationPort session = new();
            FakeConversationOutput output = new();
            ConversationTurnArbiter arbiter = NewArbiter(session, output);

            Task first = arbiter.StartTurnAsync("one", new ConversationTurnClock(clock));
            await arbiter.StartTurnAsync("two", new ConversationTurnClock(clock));
            await arbiter.StartTurnAsync("three", new ConversationTurnClock(clock));

            clock.Advance(TimeSpan.FromMilliseconds(10));
            session.ReleaseTurn();
            await first;
            await arbiter.CurrentTurn;

            Assert.Equal(["one", "two"], session.TurnsRun);
            Assert.Equal([0.010, 0.010], readings.Of(FirstToken));
        }

        [Fact(Timeout = 30_000)]
        public async Task ATurnABargeInCutShort_ReportsHowFastItAnsweredAndNoReplyEnd()
        {
            // A cut turn still says how long the model took to start, because that is true whether or
            // not the caller spoke over the answer. It reports no reply end, because it never reached
            // one: the reply-end histogram holds whole replies only, so a barge-in cannot drag the
            // distribution down by counting a reply nobody heard the end of.
            FakeTimeProvider clock = new(DateTimeOffset.UnixEpoch);
            using LatencyReadings readings = new();

            InterruptibleConversationPort session = new();
            FakeConversationOutput output = new();
            ConversationTurnArbiter arbiter = NewArbiter(session, output);

            Task turn = arbiter.StartTurnAsync("one", new ConversationTurnClock(clock));

            clock.Advance(TimeSpan.FromMilliseconds(80));
            session.YieldFirstFragment();

            // The first fragment has reached the output port, so the barge-in below lands on this
            // turn rather than on nothing.
            await session.SpokeOnce;

            _ = arbiter.Interrupt("first", TimeSpan.FromMilliseconds(40));
            session.YieldRest();

            await turn;
            await arbiter.CurrentTurn;

            Assert.Equal([0.080], readings.Of(FirstToken));
            Assert.Equal([0.080], readings.Of(FirstSpeech));
            Assert.Empty(readings.Of(ReplyEnd));
        }

        private static ConversationTurnArbiter NewArbiter(IConversationPort session, FakeConversationOutput output)
        {
            return new(
                session,
                output,
                new ConnectionTaskObserver(() => session.ConversationId, (_, _) => { }, (_, _, _) => { }, (_, _, _) => false),
                _ => { },
                _ => { },
                CancellationToken.None);
        }
    }

    /// <summary>
    /// The collection <see cref="ConversationTurnLatencyTests"/> runs in, alone.
    /// </summary>
    /// <remarks>
    /// <see cref="LatencyReadings"/> hears every turn in the process. The arbiter tests start turns
    /// too, and a turn they ran at the same moment would add readings to these asserts.
    /// </remarks>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class ConversationTurnLatencySuite
    {
        /// <summary>The name both the definition and the test class name it by.</summary>
        public const string Name = "conversation turn latency";
    }

    /// <summary>
    /// One turn the test can stop in the middle: it holds the first fragment, then holds again once
    /// that fragment has reached the output port.
    /// </summary>
    /// <remarks>
    /// The second gate is what makes a mid-reply barge-in deterministic. A turn that streamed to its
    /// end first would close its own reply, and the reply-end reading this test denies would be taken
    /// before the barge-in ever landed.
    /// </remarks>
    internal sealed class InterruptibleConversationPort : IConversationPort
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _rest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _spoke = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ConversationId => "conversation-interruptible";

        public string Stage => string.Empty;

        public bool IsComplete => false;

        public KnowledgeScope? Scope { get; set; }

        public TurnResult? LastTurn => null;

        /// <summary>Gets a task that completes once the first fragment has left for the output port.</summary>
        public Task SpokeOnce => _spoke.Task;

        /// <summary>Lets the turn produce its first fragment.</summary>
        public void YieldFirstFragment() => _first.TrySetResult();

        /// <summary>Lets the turn produce what the model had already made after the barge-in.</summary>
        public void YieldRest() => _rest.TrySetResult();

        public Task<TurnResult> RunTurnAsync(string userInput, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("the arbiter only ever streams a turn.");
        }

        public Task<TurnResult> RunTurnMessageAsync(ChatMessage userInput, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("the arbiter only ever streams a turn.");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingAsync(
            string userInput,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await _first.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, userInput);

            // Reached only after the arbiter's loop body has run for the fragment above, which is
            // where SpeakAsync is called.
            _ = _spoke.TrySetResult();

            await _rest.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, " more");
        }

        public IAsyncEnumerable<ChatResponseUpdate> RunTurnMessageStreamingAsync(
            ChatMessage userInput,
            CancellationToken cancellationToken)
        {
            return RunTurnStreamingAsync(userInput.Text ?? string.Empty, cancellationToken);
        }

        public bool Interrupt(string utteranceUntilInterrupt, TimeSpan durationUntilInterrupt, bool cutsRunningTurn = true)
        {
            return true;
        }
    }

    /// <summary>Collects every measurement this library's histograms take while it is alive.</summary>
    /// <remarks>
    /// Instrument names rather than instrument instances, because the instruments are static and are
    /// built once for the whole test assembly.
    /// </remarks>
    internal sealed class LatencyReadings : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, double Value)> _readings = [];
        private readonly Lock _gate = new();

        public LatencyReadings()
        {
            _listener.InstrumentPublished = (instrument, active) =>
            {
                if (string.Equals(instrument.Meter.Name, AgentCoreTelemetry.MeterName, StringComparison.Ordinal))
                {
                    active.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            {
                lock (_gate)
                {
                    _readings.Add((instrument.Name, value));
                }
            });

            _listener.Start();
        }

        /// <summary>Gets what one histogram was told, in order, rounded to the millisecond.</summary>
        /// <param name="instrument">The metric name.</param>
        /// <returns>The measurements, in seconds.</returns>
        public IReadOnlyList<double> Of(string instrument)
        {
            lock (_gate)
            {
                return [.. _readings
                    .Where(reading => string.Equals(reading.Instrument, instrument, StringComparison.Ordinal))
                    .Select(reading => Math.Round(reading.Value, 3))];
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}

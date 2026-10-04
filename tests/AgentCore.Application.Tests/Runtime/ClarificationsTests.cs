using AgentCore.Application.Conversation;
using Microsoft.Agents.AI;
using Xunit;
using AgentCore.Application.Runtime.Clarification;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// <see cref="Clarifications"/>: the per-conversation holder, the per-turn mark, and the
    /// probe latch and replay payload.
    /// </summary>
    public sealed class ClarificationsTests
    {
        // The probe latch is a compare-and-set. Several search-tool calls in one turn, or several
        // graph participants sharing this conversation, must cost exactly one probe.
        [Fact]
        public void ClaimProbe_ManyConcurrentCallers_ExactlyOneClaimsEachTrial()
        {
            // A single trial of three threads can pass a broken compare-and-set by luck, so this runs
            // many trials with a Barrier forcing all callers to race the same instant, and demands every
            // trial land on exactly one winner.
            const int trials = 200;
            const int callers = 8;

            for (int trial = 0; trial < trials; trial++)
            {
                Clarifications clarifications = new();
                using Barrier barrier = new(callers);
                int claims = 0;

                // A raw Thread whose body throws takes the whole test process down with it, so every
                // caller's exception is caught and reported through Assert instead of left to escape.
                Exception? escaped = null;

                Thread[] threads = new Thread[callers];
                for (int i = 0; i < callers; i++)
                {
                    threads[i] = new Thread(() =>
                    {
                        try
                        {
                            barrier.SignalAndWait();
                            if (clarifications.ClaimProbe().Won)
                            {
                                _ = Interlocked.Increment(ref claims);
                            }
                        }
                        catch (Exception failure)
                        {
                            _ = Interlocked.CompareExchange(ref escaped, failure, null);
                        }
                    });
                    threads[i].Start();
                }

                foreach (Thread thread in threads)
                {
                    thread.Join();
                }

                if (escaped is not null)
                {
                    throw escaped;
                }

                Assert.True(claims == 1, $"trial {trial}: expected exactly one claim, got {claims}.");
            }
        }

        // The caller hanging up mid-probe must fail the payload, not merely drop it, so every
        // loser waiting on it wakes rather than burning its full wait margin on a corpse.
        [Fact]
        public async Task Fail_WakesEveryWaiter_AndLeavesTheLatchClaimed()
        {
            Clarifications clarifications = new();
            Clarifications.Probe winner = clarifications.ClaimProbe();
            Assert.True(winner.Won);

            Task<IReadOnlyList<TextSearchProvider.TextSearchResult>>[] waiters = [.. Enumerable.Range(0, 5)
                .Select(_ => clarifications.ClaimProbe().WaitAsync(
                    TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))];

            winner.Fail();

            Task<IReadOnlyList<TextSearchProvider.TextSearchResult>[]> all = Task.WhenAll(waiters);
            Task completed = await Task.WhenAny(
                all, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

            Assert.Same(all, completed);

            foreach (Task<IReadOnlyList<TextSearchProvider.TextSearchResult>>? waiter in waiters)
            {
                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
            }

            // Nothing was asked and nothing was learned, so the facet must not be charged for a second
            // probe search this turn: the latch stays claimed and a fresh claim is refused.
            Assert.False(clarifications.ClaimProbe().Won);
        }

        [Fact]
        public async Task Publish_LetsEveryWaiterReplayTheSameCards()
        {
            Clarifications clarifications = new();
            Clarifications.Probe winner = clarifications.ClaimProbe();
            Assert.True(winner.Won);

            Task<IReadOnlyList<TextSearchProvider.TextSearchResult>>[] waiters = [.. Enumerable.Range(0, 3)
                .Select(_ => clarifications.ClaimProbe().WaitAsync(
                    TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))];

            IReadOnlyList<TextSearchProvider.TextSearchResult> cards = [new TextSearchProvider.TextSearchResult { Text = "note" }];
            winner.Publish(cards);

            IReadOnlyList<TextSearchProvider.TextSearchResult>[] results = await Task.WhenAll(waiters);

            Assert.All(results, result => Assert.Same(cards, result));
        }

        // BeginTurn opens a fresh turn. It clears the probe latch and its payload together, and
        // never the counter — that is per-conversation, not per-turn.
        [Fact]
        public void BeginTurn_ClearsTheLatch_AndLeavesTheCounter()
        {
            Clarifications clarifications = new();

            clarifications.ClaimProbe().Publish([]);

            clarifications.Update("brand", s => s.ProbeAsks = 3);

            clarifications.BeginTurn();

            Assert.Equal(3, clarifications.Read("brand").ProbeAsks);

            // The latch is free again.
            Assert.True(clarifications.ClaimProbe().Won);
        }

        [Fact]
        public async Task BeginTurn_ThePayloadFromTheLastTurn_IsGoneAndNotJustUnclaimed()
        {
            Clarifications clarifications = new();
            IReadOnlyList<TextSearchProvider.TextSearchResult> turn1 = [new TextSearchProvider.TextSearchResult { Text = "turn 1" }];
            clarifications.ClaimProbe().Publish(turn1);

            clarifications.BeginTurn();

            // Turn 2's own winner and loser share a payload of their own. A loser that replayed turn 1's
            // note would be F148's shape, a stale payload leaking across the boundary BeginTurn draws.
            Assert.True(clarifications.ClaimProbe().Won);
            Clarifications.Probe loser = clarifications.ClaimProbe();
            Assert.False(loser.Won);

            _ = await Assert.ThrowsAsync<TimeoutException>(
                () => loser.WaitAsync(TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken));
        }

        // A search that outlives its turn holds turn N's handle. Publishing through it must not resolve
        // the latch turn N+1 opened, or every loser of the new turn replays the old turn's cards.
        [Fact]
        public async Task Publish_FromTheTurnBefore_LeavesTheNewTurnsLatchAlone()
        {
            Clarifications clarifications = new();
            Clarifications.Probe stale = clarifications.ClaimProbe();

            clarifications.BeginTurn();

            Assert.True(clarifications.ClaimProbe().Won);
            Clarifications.Probe loser = clarifications.ClaimProbe();

            stale.Publish([new TextSearchProvider.TextSearchResult { Text = "turn 1" }]);
            stale.Fail();

            _ = await Assert.ThrowsAsync<TimeoutException>(
                () => loser.WaitAsync(TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken));
        }

        // -----------------------------------------------------------------------------------------
        // Reconnect: the ask budget is per conversation, not per session, so what one session spent must reach
        // the next one through the conversation's stored state.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void SpentAndRestoreSpent_CarryTheBudget_AndNotWhatWasNamed()
        {
            Clarifications first = new();
            first.Update("applies_to", s =>
            {
                s.ProbeAsks = 2;
                s.LastNamed = Clarifications.LastNamed.Of(new HashSet<string>(StringComparer.Ordinal) { "ct900" });
            });

            IReadOnlyDictionary<string, ConversationClarificationState> spent = first.Spent();

            Clarifications resumed = new();
            resumed.RestoreSpent(spent);

            Clarifications.SlotSnapshot slot = resumed.Read("applies_to");
            Assert.Equal(2, slot.ProbeAsks);

            // What was named belongs to a turn the reconnected caller is not in.
            Assert.Equal(Clarifications.LastNamedKind.None, slot.LastNamed.Kind);
        }

        [Fact]
        public void Spent_LeavesOutASlotNothingHasAskedAbout()
        {
            Clarifications clarifications = new();
            clarifications.Update(
                "brand",
                s => s.LastNamed = Clarifications.LastNamed.Of(new HashSet<string>(StringComparer.Ordinal) { "sole" }));

            Assert.Empty(clarifications.Spent());
        }

        // Edit-and-resend: Withdraw clears what was named, and leaves the ask counter untouched so a
        // withdrawn segment cannot buy a fresh maxAsks budget.
        [Fact]
        public void Withdraw_ClearsLastNamed_AndLeavesTheCounter()
        {
            Clarifications clarifications = new();

            clarifications.Update("brand", s =>
            {
                s.LastNamed = Clarifications.LastNamed.Of(new HashSet<string>(StringComparer.Ordinal) { "ct900", "ct900ent" });
                s.ProbeAsks = 4;
            });

            clarifications.Withdraw();

            Clarifications.SlotSnapshot after = clarifications.Read("brand");
            Assert.Equal(Clarifications.LastNamed.None, after.LastNamed);
            Assert.Equal(4, after.ProbeAsks);
        }

        [Fact]
        public void Read_ANameNeverSeenBefore_StartsAtItsZeroValues()
        {
            Clarifications clarifications = new();

            Clarifications.SlotSnapshot slot = clarifications.Read("applies_to");

            Assert.Equal(Clarifications.LastNamed.None, slot.LastNamed);
            Assert.Equal(0, slot.ProbeAsks);
        }

        [Fact]
        public void Update_TheSameName_SharesOneUnderlyingState()
        {
            Clarifications clarifications = new();

            clarifications.Update("brand", s => s.ProbeAsks = 7);

            Assert.Equal(7, clarifications.Read("brand").ProbeAsks);
        }

        // A slot's fields must move together as one step, or a concurrent reader can catch it between the
        // two writes. This is the record-what-was-named-and-move-a-counter shape, reduced to its atomicity
        // core: LastNamed is set exactly when ProbeAsks is odd, and a reader that ever sees them disagree
        // caught a torn write.
        [Fact]
        public async Task Update_ACompoundTransition_IsNeverObservedHalfApplied()
        {
            Clarifications clarifications = new();
            const string slot = "brand";
            const int iterations = 20_000;

            using CancellationTokenSource stop = new();
            int tornObservations = 0;

            Task reader = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    Clarifications.SlotSnapshot snapshot = clarifications.Read(slot);
                    bool namedIsSet = snapshot.LastNamed.Kind == Clarifications.LastNamedKind.Set;
                    bool probeAsksIsOdd = snapshot.ProbeAsks % 2 != 0;

                    if (namedIsSet != probeAsksIsOdd)
                    {
                        _ = Interlocked.Increment(ref tornObservations);
                    }
                }
            }, TestContext.Current.CancellationToken);

            Clarifications.LastNamed named = Clarifications.LastNamed.Of(new HashSet<string>(StringComparer.Ordinal) { "ct900", "ct900ent" });

            for (int i = 0; i < iterations; i++)
            {
                bool opening = i % 2 == 0;

                clarifications.Update(slot, state =>
                {
                    state.LastNamed = opening ? named : Clarifications.LastNamed.None;
                    state.ProbeAsks++;
                });
            }

            await stop.CancelAsync();
            await reader;

            Assert.Equal(0, tornObservations);
        }
    }
}

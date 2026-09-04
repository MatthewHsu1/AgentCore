using AgentCore.Application.Runtime;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// <see cref="Clarifications"/>: the per-call holder K36 describes, K41's per-turn mark, and K43's
/// probe latch and replay payload.
/// </summary>
public sealed class ClarificationsTests
{
    // -----------------------------------------------------------------------------------------
    // K43: the probe latch is a compare-and-set. Several search-tool calls in one turn, or several
    // graph participants sharing this call, must cost exactly one probe.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public void ClaimProbe_ManyConcurrentCallers_ExactlyOneClaimsEachTrial()
    {
        // A single trial of three threads can pass a broken compare-and-set by luck, so this runs
        // many trials with a Barrier forcing all callers to race the same instant, and demands every
        // trial land on exactly one winner.
        const int trials = 200;
        const int callers = 8;

        for (var trial = 0; trial < trials; trial++)
        {
            var clarifications = new Clarifications();
            using var barrier = new Barrier(callers);
            var claims = 0;

            // A raw Thread whose body throws takes the whole test process down with it, so every
            // caller's exception is caught and reported through Assert instead of left to escape.
            Exception? escaped = null;

            var threads = new Thread[callers];
            for (var i = 0; i < callers; i++)
            {
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        barrier.SignalAndWait();
                        if (clarifications.ClaimProbe().Won)
                        {
                            Interlocked.Increment(ref claims);
                        }
                    }
                    catch (Exception failure)
                    {
                        Interlocked.CompareExchange(ref escaped, failure, null);
                    }
                });
                threads[i].Start();
            }

            foreach (var thread in threads)
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

    // -----------------------------------------------------------------------------------------
    // K43: the caller hanging up mid-probe must fail the payload, not merely drop it, so every
    // loser waiting on it wakes rather than burning its full wait margin on a corpse.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public async Task Fail_WakesEveryWaiter_AndLeavesTheLatchClaimed()
    {
        var clarifications = new Clarifications();
        var winner = clarifications.ClaimProbe();
        Assert.True(winner.Won);

        var waiters = Enumerable.Range(0, 5)
            .Select(_ => clarifications.ClaimProbe().WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
            .ToArray();

        winner.Fail();

        var all = Task.WhenAll(waiters);
        var completed = await Task.WhenAny(
            all, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        Assert.Same(all, completed);

        foreach (var waiter in waiters)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        }

        // Nothing was asked and nothing was learned, so the facet must not be charged for a second
        // probe search this turn: the latch stays claimed and a fresh claim is refused.
        Assert.False(clarifications.ClaimProbe().Won);
    }

    [Fact]
    public async Task Publish_LetsEveryWaiterReplayTheSameCards()
    {
        var clarifications = new Clarifications();
        var winner = clarifications.ClaimProbe();
        Assert.True(winner.Won);

        var waiters = Enumerable.Range(0, 3)
            .Select(_ => clarifications.ClaimProbe().WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
            .ToArray();

        IReadOnlyList<TextSearchProvider.TextSearchResult> cards = [new TextSearchProvider.TextSearchResult { Text = "note" }];
        winner.Publish(cards);

        var results = await Task.WhenAll(waiters);

        Assert.All(results, result => Assert.Same(cards, result));
    }

    // -----------------------------------------------------------------------------------------
    // K41 and K43: BeginTurn opens a fresh turn. It clears the per-turn mark and the probe latch
    // and its payload together, and never the counters — those are per-call, not per-turn.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public void BeginTurn_ClearsTheLatchAndTheMark_AndLeavesTheCounters()
    {
        var clarifications = new Clarifications();

        clarifications.ClaimProbe().Publish([]);

        clarifications.Update("brand", s =>
        {
            s.AskedThisTurn = true;
            s.ProbeAsks = 3;
            s.NamedAsks = 2;
            s.ResetSpent = true;
        });

        clarifications.BeginTurn();

        var brand = clarifications.Read("brand");
        Assert.False(brand.AskedThisTurn);
        Assert.Equal(3, brand.ProbeAsks);
        Assert.Equal(2, brand.NamedAsks);
        Assert.True(brand.ResetSpent);

        // The latch is free again.
        Assert.True(clarifications.ClaimProbe().Won);
    }

    [Fact]
    public async Task BeginTurn_ThePayloadFromTheLastTurn_IsGoneAndNotJustUnclaimed()
    {
        var clarifications = new Clarifications();
        IReadOnlyList<TextSearchProvider.TextSearchResult> turn1 = [new TextSearchProvider.TextSearchResult { Text = "turn 1" }];
        clarifications.ClaimProbe().Publish(turn1);

        clarifications.BeginTurn();

        // Turn 2's own winner and loser share a payload of their own. A loser that replayed turn 1's
        // note would be F148's shape, a stale payload leaking across the boundary BeginTurn draws.
        Assert.True(clarifications.ClaimProbe().Won);
        var loser = clarifications.ClaimProbe();
        Assert.False(loser.Won);

        await Assert.ThrowsAsync<TimeoutException>(
            () => loser.WaitAsync(TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken));
    }

    // -----------------------------------------------------------------------------------------
    // A search that outlives its turn holds turn N's handle. Publishing through it must not resolve
    // the latch turn N+1 opened, or every loser of the new turn replays the old turn's cards.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public async Task Publish_FromTheTurnBefore_LeavesTheNewTurnsLatchAlone()
    {
        var clarifications = new Clarifications();
        var stale = clarifications.ClaimProbe();

        clarifications.BeginTurn();

        Assert.True(clarifications.ClaimProbe().Won);
        var loser = clarifications.ClaimProbe();

        stale.Publish([new TextSearchProvider.TextSearchResult { Text = "turn 1" }]);
        stale.Fail();

        await Assert.ThrowsAsync<TimeoutException>(
            () => loser.WaitAsync(TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken));
    }

    // -----------------------------------------------------------------------------------------
    // Reconnect: the ask budget is per call, not per session, so what one session spent must reach
    // the next one through the call's stored state.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public void SpentAndRestoreSpent_CarryTheBudget_AndNotWhatWasNamed()
    {
        var first = new Clarifications();
        first.Update("applies_to", s =>
        {
            s.ProbeAsks = 2;
            s.NamedAsks = 1;
            s.ResetSpent = true;
            s.Pending = ["ct900", "ct900ent"];
            s.LastNamed = Clarifications.LastNamed.Of(new HashSet<string>(StringComparer.Ordinal) { "ct900" });
        });

        var spent = first.Spent();

        var resumed = new Clarifications();
        resumed.RestoreSpent(spent);

        var slot = resumed.Read("applies_to");
        Assert.Equal(2, slot.ProbeAsks);
        Assert.Equal(1, slot.NamedAsks);
        Assert.True(slot.ResetSpent);

        // The pending list and what was named belong to a turn the reconnected caller is not in.
        Assert.Null(slot.Pending);
        Assert.Equal(Clarifications.LastNamedKind.None, slot.LastNamed.Kind);
    }

    [Fact]
    public void Spent_LeavesOutASlotNothingHasAskedAbout()
    {
        var clarifications = new Clarifications();
        clarifications.Update("brand", s => s.Pending = ["sole", "spirit"]);

        Assert.Empty(clarifications.Spent());
    }

    // -----------------------------------------------------------------------------------------
    // §7's ask is staged: reads see it at once, because step 1 and the probe's K41 skip both act on
    // it this turn, but only a turn that answered the caller keeps it.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public void Ask_IsVisibleAtOnce_ButDroppedByTheNextBeginTurn()
    {
        var clarifications = new Clarifications();
        var named = Clarifications.LastNamed.Of(new HashSet<string>(StringComparer.Ordinal) { "ct900" });

        clarifications.Ask("applies_to", named, spendsReset: false);

        var asked = clarifications.Read("applies_to");
        Assert.Equal(1, asked.NamedAsks);
        Assert.True(asked.AskedThisTurn);
        Assert.True(named.Names(asked.LastNamed));

        clarifications.BeginTurn();

        var dropped = clarifications.Read("applies_to");
        Assert.Equal(0, dropped.NamedAsks);
        Assert.Equal(Clarifications.LastNamedKind.None, dropped.LastNamed.Kind);
    }

    [Fact]
    public void CommitAsks_KeepsTheAsk_AcrossTheTurnBoundary()
    {
        var clarifications = new Clarifications();
        var named = Clarifications.LastNamed.Of(new HashSet<string>(StringComparer.Ordinal) { "ct900" });

        clarifications.Ask("applies_to", named, spendsReset: false);
        clarifications.CommitAsks();
        clarifications.BeginTurn();

        var kept = clarifications.Read("applies_to");
        Assert.Equal(1, kept.NamedAsks);
        Assert.True(named.Names(kept.LastNamed));
    }

    [Fact]
    public void Ask_ThatSpendsTheReset_ReturnsTheCounterToOne()
    {
        var clarifications = new Clarifications();
        clarifications.Update("applies_to", s => s.NamedAsks = 3);

        clarifications.Ask("applies_to", Clarifications.LastNamed.TooMany, spendsReset: true);

        var snapshot = clarifications.Read("applies_to");
        Assert.Equal(1, snapshot.NamedAsks);
        Assert.True(snapshot.ResetSpent);
    }

    // -----------------------------------------------------------------------------------------
    // Edit-and-resend: Withdraw clears what was named and what is pending, and leaves the ask
    // counters untouched so a withdrawn segment cannot buy a fresh maxAsks budget.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public void Withdraw_ClearsPendingAndLastNamed_AndLeavesTheCounters()
    {
        var clarifications = new Clarifications();

        clarifications.Update("brand", s =>
        {
            s.Pending = ["ct900", "ct900ent"];
            s.LastNamed = Clarifications.LastNamed.Of(new HashSet<string>(StringComparer.Ordinal) { "ct900", "ct900ent" });
            s.ProbeAsks = 4;
            s.NamedAsks = 1;
            s.ResetSpent = true;
            s.AskedThisTurn = true;
        });

        clarifications.Withdraw();

        var after = clarifications.Read("brand");
        Assert.Null(after.Pending);
        Assert.Equal(Clarifications.LastNamed.None, after.LastNamed);
        Assert.Equal(4, after.ProbeAsks);
        Assert.Equal(1, after.NamedAsks);
        Assert.True(after.ResetSpent);

        // BeginTurn's mark is a different lifetime; Withdraw must not touch it.
        Assert.True(after.AskedThisTurn);
    }

    [Fact]
    public void Read_ANameNeverSeenBefore_StartsAtItsZeroValues()
    {
        var clarifications = new Clarifications();

        var slot = clarifications.Read("applies_to");

        Assert.Null(slot.Pending);
        Assert.Equal(Clarifications.LastNamed.None, slot.LastNamed);
        Assert.Equal(0, slot.ProbeAsks);
        Assert.Equal(0, slot.NamedAsks);
        Assert.False(slot.ResetSpent);
        Assert.False(slot.AskedThisTurn);
    }

    [Fact]
    public void Update_TheSameName_SharesOneUnderlyingState()
    {
        var clarifications = new Clarifications();

        clarifications.Update("brand", s => s.ProbeAsks = 7);

        Assert.Equal(7, clarifications.Read("brand").ProbeAsks);
    }

    // -----------------------------------------------------------------------------------------
    // §7/§8's compound transitions (K36): a slot's fields must move together as one step, or a
    // concurrent reader can catch it between the two writes. This is the §8-step-6 shape - set the
    // pending list and move a counter - reduced to its atomicity core: Pending is set exactly when
    // ProbeAsks is odd, and a reader that ever sees them disagree caught a torn write.
    // -----------------------------------------------------------------------------------------
    [Fact]
    public async Task Update_ACompoundTransition_IsNeverObservedHalfApplied()
    {
        var clarifications = new Clarifications();
        const string slot = "brand";
        const int iterations = 20_000;

        using var stop = new CancellationTokenSource();
        var tornObservations = 0;

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var snapshot = clarifications.Read(slot);
                var pendingIsSet = snapshot.Pending is not null;
                var probeAsksIsOdd = snapshot.ProbeAsks % 2 != 0;

                if (pendingIsSet != probeAsksIsOdd)
                {
                    Interlocked.Increment(ref tornObservations);
                }
            }
        }, TestContext.Current.CancellationToken);

        for (var i = 0; i < iterations; i++)
        {
            var opening = i % 2 == 0;

            clarifications.Update(slot, state =>
            {
                state.Pending = opening ? ["ct900", "ct900ent"] : null;
                state.ProbeAsks++;
            });
        }

        await stop.CancelAsync();
        await reader;

        Assert.Equal(0, tornObservations);
    }
}

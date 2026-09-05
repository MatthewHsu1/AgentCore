using System.Collections.Concurrent;
using AgentCore.Application.State;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.Application.Tests.State;

/// <summary>
/// <see cref="VocabularyCache"/>: the per-slot immutable vocabulary, and <see cref="VocabularyCache.Replace"/>'s
/// section 10 refusals.
/// </summary>
public sealed class VocabularyCacheTests
{
    [Fact]
    public void Replace_ZeroValues_ThrowsNamingTheSlot()
    {
        var cache = new VocabularyCache();

        var exception = Assert.Throws<VocabularyException>(() => cache.Replace("machine", [], maxValues: 100));

        Assert.Equal("machine", exception.Slot);
    }

    [Fact]
    public void Replace_OnlyTheWildcardValue_ThrowsAsZeroValuesAfterStripping()
    {
        var cache = new VocabularyCache();

        Assert.Throws<VocabularyException>(
            () => cache.Replace("machine", ["*"], maxValues: 100, wildcardValue: "*"));
    }

    [Fact]
    public void Replace_CountEqualsMaxValues_ThrowsBecauseATruncationCannotBeToldFromACompleteRead()
    {
        var cache = new VocabularyCache();

        Assert.Throws<VocabularyException>(() => cache.Replace("machine", ["a", "b"], maxValues: 2));
    }

    [Fact]
    public void Replace_CountExceedsMaxValues_StillThrows()
    {
        // K4's own truncation check is a rank cut against maxValues, not an exact-count comparison:
        // a store that ignored its own limit and returned more than maxValues must not slip past it.
        var cache = new VocabularyCache();

        Assert.Throws<VocabularyException>(() => cache.Replace("machine", ["a", "b", "c"], maxValues: 2));
    }

    [Fact]
    public void Replace_TwoValuesFoldAlike_ThrowsNamingBoth()
    {
        var cache = new VocabularyCache();

        var exception = Assert.Throws<VocabularyException>(
            () => cache.Replace("machine", ["T900", "t900"], maxValues: 100));

        Assert.Contains("T900", exception.Message, StringComparison.Ordinal);
        Assert.Contains("t900", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Replace_ValueFoldsToEmpty_ThrowsAndDoesNotDependOnComposition()
    {
        // "***" has nothing for NFC to compose and nothing for Keep to keep, so this refusal
        // fires identically whether or not the runtime can compose Unicode (K44 governs
        // composition; this row exercises the K31/K46 refusal on its own).
        var cache = new VocabularyCache();

        var exception = Assert.Throws<VocabularyException>(
            () => cache.Replace("machine", ["***"], maxValues: 100));

        Assert.Contains("***", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Replace_WildcardValueStripped_DoesNotFalselyTriggerTheFoldsToEmptyRefusal()
    {
        // If "*" were not removed before the empty-fold check (K6), it would fold to "" itself
        // and wrongly refuse startup on a document that is otherwise perfectly fine.
        var cache = new VocabularyCache();

        cache.Replace("machine", ["*", "T900"], maxValues: 100, wildcardValue: "*");

        var view = cache.Snapshot()["machine"];
        Assert.Equal(["T900"], view.Originals);
    }

    [Fact]
    public void Replace_WildcardValueFoldsLikeASurvivor_DoesNotFalselyTriggerTheFoldingCollisionRefusal()
    {
        // K6: the wildcard is stripped before the *collision* check specifically, not only before
        // the empty-fold check. "ALL" and "all" fold alike, so if the strip ran after the collision
        // check (or not at all), the untouched wildcard sentinel "ALL" would collide with the real
        // survivor "all" and Replace would wrongly refuse a document that is otherwise fine.
        var cache = new VocabularyCache();

        cache.Replace("machine", ["ALL", "all"], maxValues: 100, wildcardValue: "ALL");

        var view = cache.Snapshot()["machine"];
        Assert.Equal(["all"], view.Originals);
    }

    [Fact]
    public void Replace_MixedCaseSpacedHyphenatedValue_MapsBackToTheOriginal()
    {
        var cache = new VocabularyCache();

        cache.Replace("machine", ["North-900 Pro"], maxValues: 100);

        var view = cache.Snapshot()["machine"];
        var normalised = VocabularyFold.Fold("North-900 Pro");
        Assert.Equal("North-900 Pro", view.NormalisedToOriginal[normalised]);
    }

    [Fact]
    public void Replace_ExactlyOneValue_Succeeds()
    {
        var cache = new VocabularyCache();

        cache.Replace("machine", ["T900"], maxValues: 100);

        Assert.Equal(["T900"], cache.Snapshot()["machine"].Originals);
    }

    [Fact]
    public void Snapshot_TakenBeforeReplace_IsUnaffectedByALaterReplace()
    {
        var cache = new VocabularyCache();
        cache.Replace("machine", ["T900"], maxValues: 100);

        var before = cache.Snapshot();
        var viewBefore = before["machine"];
        cache.Replace("machine", ["F80"], maxValues: 100);
        var after = cache.Snapshot();

        // "Byte-identical" (the acceptance clause's word) means the same object, not merely an
        // equal one: Replace swaps the outer dictionary reference rather than mutating an entry,
        // so the view a caller already read must never change under it.
        Assert.Same(viewBefore, before["machine"]);
        Assert.NotSame(before, after);
        Assert.Equal(["T900"], before["machine"].Originals);
        Assert.Equal(["F80"], after["machine"].Originals);
    }

    [Fact]
    public void Snapshot_BeforeAnyReplace_HasNoEntryForTheSlot()
    {
        var cache = new VocabularyCache();

        Assert.False(cache.Snapshot().ContainsKey("machine"));
    }

    [Fact]
    public void LastGoodAt_BeforeAnyReplace_IsNull()
    {
        var cache = new VocabularyCache();

        Assert.Null(cache.LastGoodAt("machine"));
    }

    [Fact]
    public void LastGoodAt_AfterAReplace_IsTheClocksTime()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var cache = new VocabularyCache(clock);

        cache.Replace("machine", ["T900"], maxValues: 100);

        Assert.Equal(clock.GetUtcNow(), cache.LastGoodAt("machine"));
    }

    [Fact]
    public async Task Snapshot_ReadWhileARefreshIsReplacing_NeverThrows()
    {
        var cache = new VocabularyCache();
        cache.Replace("machine", ["T900"], maxValues: 100);

        using ManualResetEventSlim reading = new();
        using CancellationTokenSource stop = new();

        var reader = Task.Run(
            () =>
            {
                reading.Set();
                while (!stop.IsCancellationRequested)
                {
                    var snapshot = cache.Snapshot();
                    _ = snapshot["machine"].Originals.Count;
                }
            },
            TestContext.Current.CancellationToken);

        reading.Wait(TestContext.Current.CancellationToken);

        for (var round = 0; round < 200; round++)
        {
            cache.Replace("machine", [round % 2 == 0 ? "T900" : "F80"], maxValues: 100);
        }

        await stop.CancelAsync();
        await reader;
    }

    [Fact]
    public async Task Replace_TwoThreadsRacingTheSameSlot_SnapshotAndLastGoodAtAgreeOnTheLastWriter()
    {
        // Replace writes the new view and the new LastGoodAt inside one lock. If that regressed to
        // two separate critical sections, a thread whose GetUtcNow() call landed after a later
        // thread's view swap could leave Snapshot() and LastGoodAt() naming two different writes.
        //
        // Which thread's write is "last" is otherwise unobservable from outside the lock, so
        // TaggingTimeProvider records, on every clock read, which value this thread is currently
        // writing. Because GetUtcNow() only ever runs from inside Replace's own lock, the recorded
        // ticks reconstruct the true lock-acquisition order regardless of how the OS scheduled the
        // two threads — the assertion does not depend on which thread physically ran last.
        const int Rounds = 300;
        var timeout = TimeSpan.FromSeconds(10);
        var provider = new TaggingTimeProvider();
        var cache = new VocabularyCache(provider);

        using ManualResetEventSlim start = new();

        // Two barriers, not one: startOfRound re-synchronises both threads before either writes,
        // so neither can pull ahead and finish its own tail alone — every round has both threads
        // genuinely contending for the lock at close to the same instant. afterWrites re-joins them
        // once both threads' Replace calls for the round have returned, which is a quiescent point —
        // no writer is in flight and neither thread can start the next round's Replace until this
        // phase's post-action returns — so the invariant can be checked here every round instead of
        // only once at the very end, where it only ever exercised the last round's own timing.
        using Barrier startOfRound = new(2);
        using Barrier afterWrites = new(2, _ => AssertQuiescentAgreement(provider, cache));

        void RaceWithTag(string prefix)
        {
            start.Wait();
            for (var index = 0; index < Rounds; index++)
            {
                Assert.True(startOfRound.SignalAndWait(timeout), "Timed out starting a round.");

                var value = prefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                TaggingTimeProvider.Tag(value);
                cache.Replace("machine", [value], maxValues: 1_000_000);

                // If Replace ever threw, this thread would never reach here and the other thread
                // would otherwise wait forever for a partner that is not coming — the timeout turns
                // that into a failed assertion instead of a hung test.
                Assert.True(afterWrites.SignalAndWait(timeout), "Timed out at the round's quiescent checkpoint.");
            }
        }

        var threadA = Task.Run(() => RaceWithTag("A"), TestContext.Current.CancellationToken);
        var threadB = Task.Run(() => RaceWithTag("B"), TestContext.Current.CancellationToken);
        start.Set();
        await Task.WhenAll(threadA, threadB);

        Assert.Equal(Rounds * 2, provider.Calls.Count);
    }

    private static void AssertQuiescentAgreement(TaggingTimeProvider provider, VocabularyCache cache)
    {
        var last = provider.Calls.OrderByDescending(call => call.At).First();

        Assert.Equal([last.Value], cache.Snapshot()["machine"].Originals);
        Assert.Equal(last.At, cache.LastGoodAt("machine"));
    }

    private sealed class TaggingTimeProvider : TimeProvider
    {
        [ThreadStatic]
        private static string? _currentValue;

        private readonly ConcurrentQueue<(string Value, DateTimeOffset At)> _calls = new();
        private long _ticks;

        public IReadOnlyList<(string Value, DateTimeOffset At)> Calls => [.. _calls];

        public static void Tag(string value) => _currentValue = value;

        public override DateTimeOffset GetUtcNow()
        {
            var at = DateTimeOffset.UnixEpoch.AddTicks(Interlocked.Increment(ref _ticks));
            _calls.Enqueue((_currentValue ?? string.Empty, at));
            return at;
        }
    }
}

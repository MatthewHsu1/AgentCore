using AgentCore.Application.Calls;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime;

/// <summary>
/// One call's memory of what the knowledge probe has asked and named, and the per-turn latch that
/// lets several search-tool calls in one turn share a single probe (K36, K43).
/// </summary>
internal sealed class Clarifications
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, SlotState> _slots = new(StringComparer.Ordinal);

    private Probe? _probe;

    /// <summary>Reads a consistent snapshot of one slot's two fields, as of one lock acquisition.</summary>
    /// <param name="name">The slot's name.</param>
    /// <returns>
    /// The snapshot. Every field in it was read under the same lock acquisition, so a concurrent
    /// <see cref="Update"/> is either fully before it or fully after it — never half of one and half
    /// of the other.
    /// </returns>
    internal SlotSnapshot Read(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        lock (_gate)
        {
            return Snapshot(GetOrAdd(name));
        }
    }

    /// <summary>
    /// Runs a compound transition on one slot as a single atomic step: one lock acquisition covers
    /// every field <paramref name="mutate"/> touches, so a reader's <see cref="Read"/> can never
    /// observe the slot mid-transition.
    /// </summary>
    /// <param name="name">The slot's name.</param>
    /// <param name="mutate">
    /// What to change. Runs while this object's lock is held — keep it free of anything that could
    /// itself try to take the lock (including a call back into <see cref="Read"/> or
    /// <see cref="Update"/>), or of anything slow, since every other slot is blocked for as long
    /// as this runs.
    /// </param>
    internal void Update(string name, Action<SlotState> mutate)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            mutate(GetOrAdd(name));
        }
    }

    /// <summary>
    /// Claims this turn's one probe search. Several search-tool calls in one turn, or several graph
    /// participants sharing this call, race here, and exactly one wins.
    /// </summary>
    /// <returns>
    /// The turn's probe. <see cref="Probe.Won"/> is true for the one caller that must run the search
    /// and then report through <see cref="Probe.Publish"/> or <see cref="Probe.Fail"/>, and false for
    /// every other caller this turn, which replays the winner's outcome through
    /// <see cref="Probe.WaitAsync"/> instead. The handle names the turn's own payload, so a call
    /// that outlives its turn publishes to a latch nothing is waiting on rather than to the next
    /// turn's. A winner must resolve the latch on every path out, including a throw, or the turn's
    /// other callers wait out their full margin for an outcome that is never coming.
    /// </returns>
    internal Probe ClaimProbe()
    {
        lock (_gate)
        {
            if (_probe is { } claimed)
            {
                return claimed.AsLoser();
            }

            _probe = new Probe(
                new TaskCompletionSource<IReadOnlyList<TextSearchProvider.TextSearchResult>>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
                won: true);

            return _probe;
        }
    }

    /// <summary>
    /// Opens a new turn: drops the turn's probe latch, so a fresh turn claims and replays its own
    /// probe rather than the turn before it.
    /// </summary>
    /// <remarks>
    /// Must run exactly once per turn, from <c>CallSession.BeginTurn</c> and nowhere else.
    /// <c>CallSession.EnterAmbients</c> reopens its state scope once per streaming step.
    /// </remarks>
    internal void BeginTurn()
    {
        lock (_gate)
        {
            _probe = null;
        }
    }

    /// <summary>
    /// Takes back what an edit-and-resend withdrew: every slot's record of what was last named to
    /// the caller. The ask counter is left alone — the caller heard what they heard, and clearing it
    /// would let the withdrawn segment buy a fresh <c>maxAsks</c> budget.
    /// </summary>
    internal void Withdraw()
    {
        lock (_gate)
        {
            foreach (var state in _slots.Values)
            {
                state.LastNamed = LastNamed.None;
            }
        }
    }

    /// <summary>Reads what every slot has spent of its ask budget, for the call's stored state.</summary>
    /// <returns>
    /// One entry per slot that has spent anything. A slot that has never been asked about is left
    /// out, so a call that reached no ambiguity stores nothing.
    /// </returns>
    internal IReadOnlyDictionary<string, CallClarificationState> Spent()
    {
        lock (_gate)
        {
            Dictionary<string, CallClarificationState> spent = new(StringComparer.Ordinal);

            foreach (var (name, state) in _slots)
            {
                if (state.ProbeAsks == 0)
                {
                    continue;
                }

                spent[name] = new CallClarificationState { ProbeAsks = state.ProbeAsks };
            }

            return spent;
        }
    }

    /// <summary>Puts back what an earlier session of this call spent of its ask budget.</summary>
    /// <param name="spent">What <see cref="Spent"/> read from that session.</param>
    /// <remarks>
    /// The record of what was last named is deliberately not restored: it belongs to a turn the
    /// reconnected caller is no longer in. Only the budget has to survive, and for the reason
    /// <see cref="Withdraw"/> gives — a caller who drops and comes back must not buy a fresh
    /// <c>maxAsks</c> and hear the same question all over again.
    /// </remarks>
    internal void RestoreSpent(IReadOnlyDictionary<string, CallClarificationState> spent)
    {
        ArgumentNullException.ThrowIfNull(spent);

        lock (_gate)
        {
            foreach (var (name, stored) in spent)
            {
                GetOrAdd(name).ProbeAsks = stored.ProbeAsks;
            }
        }
    }

    /// <summary>Reads or creates one slot's state. Callers must already hold <see cref="_gate"/>.</summary>
    private SlotState GetOrAdd(string name)
    {
        if (!_slots.TryGetValue(name, out var state))
        {
            state = new SlotState();
            _slots.Add(name, state);
        }

        return state;
    }

    private static SlotSnapshot Snapshot(SlotState state) => new(state.LastNamed, state.ProbeAsks);

    /// <summary>What decided the wildcard a slot's last named record holds.</summary>
    internal enum LastNamedKind
    {
        /// <summary>Nothing has been named to the caller for this slot.</summary>
        None,

        /// <summary>The exact candidate set last named.</summary>
        Set,

        /// <summary>The candidate set was above <c>maxCandidates</c>, so no list was ever spoken.</summary>
        TooMany,
    }

    /// <summary>
    /// One turn's probe latch, held by the caller that claimed it.
    /// </summary>
    /// <remarks>
    /// The payload travels on the handle rather than being looked up again on publish, so a search
    /// that outlives its turn — an abandoned tool task, a cancellation callback that runs late —
    /// resolves the latch of the turn it belongs to and cannot touch the one the next turn opened.
    /// </remarks>
    internal sealed class Probe
    {
        private readonly TaskCompletionSource<IReadOnlyList<TextSearchProvider.TextSearchResult>> _payload;

        internal Probe(
            TaskCompletionSource<IReadOnlyList<TextSearchProvider.TextSearchResult>> payload, bool won)
        {
            _payload = payload;
            Won = won;
        }

        /// <summary>Whether this caller is the one that must run the search and publish its outcome.</summary>
        internal bool Won { get; }

        /// <summary>Publishes the winner's probe result for every waiting caller to replay.</summary>
        /// <param name="cards">What the probe's search returned.</param>
        internal void Publish(IReadOnlyList<TextSearchProvider.TextSearchResult> cards)
        {
            ArgumentNullException.ThrowIfNull(cards);

            _payload.TrySetResult(cards);
        }

        /// <summary>
        /// Fails the outcome so every waiting caller wakes rather than burning its full wait margin on
        /// a probe that will never answer. The latch stays claimed: nothing was asked and nothing was
        /// learned, so this turn must not run a second probe search over it. Safe to call after
        /// <see cref="Publish"/>, which is what lets a winner arm this as its unconditional way out.
        /// </summary>
        internal void Fail() => _payload.TrySetCanceled();

        /// <summary>Waits for the turn's probe winner to publish an outcome.</summary>
        /// <param name="timeout">How long to wait beyond the winner's own probe deadline.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>What the winner's search found.</returns>
        /// <exception cref="OperationCanceledException">
        /// The winner's probe was failed by <see cref="Fail"/>, or <paramref name="cancellationToken"/> was.
        /// </exception>
        /// <exception cref="TimeoutException">Nobody published an outcome within <paramref name="timeout"/>.</exception>
        internal Task<IReadOnlyList<TextSearchProvider.TextSearchResult>> WaitAsync(
            TimeSpan timeout, CancellationToken cancellationToken)
            => _payload.Task.WaitAsync(timeout, cancellationToken);

        /// <summary>The same latch, handed to a caller that did not claim it.</summary>
        internal Probe AsLoser() => new(_payload, won: false);
    }

    /// <summary>One slot's record of what was last named to the caller (K37).</summary>
    internal readonly record struct LastNamed(LastNamedKind Kind, IReadOnlySet<string>? Values)
    {
        /// <summary>Nothing has been named yet.</summary>
        internal static readonly LastNamed None = new(LastNamedKind.None, null);

        /// <summary>The candidate set was above <c>maxCandidates</c>; no list was spoken.</summary>
        internal static readonly LastNamed TooMany = new(LastNamedKind.TooMany, null);

        /// <summary>Records the exact set that was named.</summary>
        internal static LastNamed Of(IReadOnlySet<string> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            return new LastNamed(LastNamedKind.Set, values);
        }

        /// <summary>What the probe would record for one candidate set (K37).</summary>
        /// <param name="candidates">The candidates the probe is about to name.</param>
        /// <param name="maxCandidates">The document's <c>maxCandidates</c>.</param>
        /// <returns>
        /// <see cref="TooMany"/> when the set is over the cap, so no list is spoken; otherwise the
        /// exact set, under the ordinal comparer <see cref="Names"/> relies on.
        /// </returns>
        internal static LastNamed For(IReadOnlyCollection<string> candidates, int maxCandidates)
        {
            ArgumentNullException.ThrowIfNull(candidates);

            return candidates.Count > maxCandidates
                ? TooMany
                : Of(new HashSet<string>(candidates, StringComparer.Ordinal));
        }

        /// <summary>
        /// Whether this record names the same thing as <paramref name="other"/>. Two <c>too-many</c>
        /// records match regardless of their underlying sets: a list neither ever spoke cannot be
        /// compared, and two different over-<c>maxCandidates</c> sets are meant to read as the
        /// identical question (K34).
        /// </summary>
        /// <param name="other">The record to compare against.</param>
        /// <returns><see langword="true"/> when the caller would hear the same question twice.</returns>
        internal bool Names(LastNamed other)
            => Kind == other.Kind && (Kind != LastNamedKind.Set || Values!.SetEquals(other.Values!));
    }

    /// <summary>A consistent, point-in-time copy of one slot's two fields (see <see cref="Read"/>).</summary>
    internal readonly record struct SlotSnapshot(LastNamed LastNamed, int ProbeAsks);

    /// <summary>
    /// One slot's mutable ambiguity state. Read through <see cref="Read"/> and written through
    /// <see cref="Update"/> — never directly, except from inside an <see cref="Update"/> callback,
    /// which already holds the lock.
    /// </summary>
    internal sealed class SlotState
    {
        internal LastNamed LastNamed = LastNamed.None;

        internal int ProbeAsks;
    }
}

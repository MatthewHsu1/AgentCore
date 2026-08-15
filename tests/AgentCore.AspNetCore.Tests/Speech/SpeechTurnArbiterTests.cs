using System.Runtime.CompilerServices;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.Speech;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Speech;

/// <summary>
/// The three questions the arbiter answers, with no socket and no vendor frame in sight.
/// </summary>
public sealed class SpeechTurnArbiterTests
{
    [Fact(Timeout = 30_000)]
    public async Task ASecondUtteranceDuringATurn_IsHeldAndRunsWhenTheTurnEnds()
    {
        // One is held. The turn in flight finishes, and the held one runs inside its own ending.
        var session = new ScriptedConversationPort();
        var output = new FakeSpeechOutput();
        var arbiter = NewArbiter(session, output);

        var first = arbiter.StartTurnAsync("one");
        await arbiter.StartTurnAsync("two");

        session.ReleaseTurn();
        await first;
        await arbiter.CurrentTurn;

        Assert.Equal(["one", "two"], session.TurnsRun);
    }

    [Fact(Timeout = 30_000)]
    public async Task AThirdUtteranceDuringATurn_IsDroppedRatherThanQueued()
    {
        // A queue of held speech would answer questions the caller has already moved past.
        var session = new ScriptedConversationPort();
        var output = new FakeSpeechOutput();
        var arbiter = NewArbiter(session, output);

        var first = arbiter.StartTurnAsync("one");
        await arbiter.StartTurnAsync("two");
        await arbiter.StartTurnAsync("three");

        session.ReleaseTurn();
        await first;
        await arbiter.CurrentTurn;

        Assert.Equal(["one", "two"], session.TurnsRun);
    }

    [Fact(Timeout = 30_000)]
    public async Task ABargeInDuringTheHeldPromptWindow_CutsTheTurnTheCallerWasHearing()
    {
        // Turn one finished streaming and turn two started inside its ending, but the caller is
        // still hearing turn one. The record belongs to turn one, and turn two must keep speaking.
        var session = new ScriptedConversationPort();
        var output = new FakeSpeechOutput();
        var arbiter = NewArbiter(session, output);

        // Opened before the first turn, not during it: this test needs turn one to finish streaming
        // on its own, and nothing here ever comes back to open a gate the two tests above open while
        // a turn is deliberately held in flight.
        session.ReleaseTurn();

        await arbiter.StartTurnAsync("one");
        await arbiter.CurrentTurn;

        var recorded = arbiter.Interrupt("half a reply", TimeSpan.FromMilliseconds(600));

        Assert.True(recorded);
        Assert.Equal("half a reply", session.LastHeardText);
        Assert.Equal(1, output.Stops);

        // The turn the caller heard is the turn running now, so the barge-in cuts it. The other
        // half of that comparison — the held-prompt window this test's name describes, where the
        // two are different turns — is pinned by the test below.
        Assert.True(session.LastCutRunningTurn);
    }

    [Fact(Timeout = 30_000)]
    public async Task ABargeInWhileTheHeldTurnHasSaidNothing_BelongsToTheTurnTheCallerWasHearing()
    {
        // The rule the arbiter is hardest to get right. Turn one finished streaming and turn two
        // started inside its ending, but turn two has not produced one word, so the caller is still
        // hearing turn one. cutsRunningTurn must answer false there — turn two is not the turn to
        // cut, and it must keep speaking — and true once turn two is itself what the caller heard.
        var session = new ScriptedConversationPort();
        var output = new FakeSpeechOutput();
        var arbiter = NewArbiter(session, output);

        var first = arbiter.StartTurnAsync("one");
        await arbiter.StartTurnAsync("two");

        // Turn one only. Turn two starts inside turn one's own ending and stops at its own gate,
        // having said nothing, which is the window this test exists for.
        session.ReleaseTurn(1);
        await first;

        Assert.True(arbiter.Interrupt("first", TimeSpan.FromMilliseconds(640)));
        Assert.False(session.LastCutRunningTurn);

        // Turn two kept speaking, and closed its own reply: the barge-in belonged to turn one, so
        // nothing silenced a turn the caller had not heard.
        session.ReleaseTurn(2);
        await arbiter.CurrentTurn;

        Assert.Contains("two", output.Spoken);
        Assert.Equal(2, output.Completions);

        // And now the turn the caller has heard is the turn running now, so the same comparison
        // answers the other way.
        Assert.True(arbiter.Interrupt("second", TimeSpan.FromMilliseconds(120)));
        Assert.True(session.LastCutRunningTurn);
    }

    [Fact(Timeout = 30_000)]
    public async Task ASecondCallNamedMidTurn_StillHoldsTheNextUtteranceBehindTheTurnInFlight()
    {
        // One transport runs one turn at a time, and a vendor that names the call a second time
        // does not get to start a second reply alongside the first. The prompt that arrives after
        // the rename is held, and it runs against the call named last.
        var replaced = new ScriptedConversationPort();
        var named = new ScriptedConversationPort();
        var output = new FakeSpeechOutput();
        var arbiter = NewArbiter(replaced, output);

        var running = arbiter.StartTurnAsync("one");

        arbiter.Rebind(named);
        await arbiter.StartTurnAsync("two");

        Assert.Empty(named.TurnsRun);

        replaced.ReleaseTurn();
        named.ReleaseTurn();
        await running;
        await arbiter.CurrentTurn;

        Assert.Equal(["one"], replaced.TurnsRun);
        Assert.Equal(["two"], named.TurnsRun);
    }

    private static SpeechTurnArbiter NewArbiter(ScriptedConversationPort session, FakeSpeechOutput output)
        => new(
            session,
            output,
            new ConnectionTaskObserver(() => session.CallId, (_, _) => { }, (_, _, _) => { }, (_, _, _) => false),
            _ => { },
            _ => { },
            CancellationToken.None);
}

/// <summary>
/// One call the test drives by hand: it records the turns it was asked to run, holds each of them
/// on one gate the test opens, and remembers what a barge-in told it.
/// </summary>
/// <remarks>
/// The gate is what makes the two holding tests deterministic. Without it a turn would run to its
/// end inside <c>StartTurnAsync</c> itself, and a second utterance would find no turn in flight to
/// be held behind — which is the one condition those tests exist to create.
/// </remarks>
internal sealed class ScriptedConversationPort : IConversationPort
{
    private readonly Dictionary<int, TaskCompletionSource> _gates = [];
    private readonly List<string> _turnsRun = [];
    private readonly Lock _lock = new();
    private bool _allReleased;
    private int _turnsStarted;

    /// <inheritdoc />
    public string CallId => "call-scripted";

    /// <inheritdoc />
    public string Stage => string.Empty;

    /// <inheritdoc />
    public bool IsComplete => false;

    /// <inheritdoc />
    public TurnResult? LastTurn => null;

    /// <summary>Gets what the caller was told it heard, or null before a barge-in.</summary>
    public string? LastHeardText { get; private set; }

    /// <summary>Gets how much of the reply the caller was told had played, or null before a barge-in.</summary>
    public TimeSpan? LastPlayedDuration { get; private set; }

    /// <summary>Gets whether the last barge-in said it cut the turn running now.</summary>
    public bool? LastCutRunningTurn { get; private set; }

    /// <summary>Gets the text of every turn this port was asked to run, in order.</summary>
    public IReadOnlyList<string> TurnsRun
    {
        get { lock (_lock) { return [.. _turnsRun]; } }
    }

    /// <summary>Opens the gate of every turn, the ones already waiting and every later one.</summary>
    public void ReleaseTurn()
    {
        lock (_lock)
        {
            _allReleased = true;
            foreach (var gate in _gates.Values)
            {
                gate.TrySetResult();
            }
        }
    }

    /// <summary>Opens the gate of one turn only, named by the order it started in, from one.</summary>
    /// <param name="ordinal">The turn to release: 1 is the first turn this port was asked to run.</param>
    /// <remarks>
    /// One turn at a time is what the arbiter promises, so releasing turn one alone is how a test
    /// reaches the window the arbiter's hardest rule lives in: turn one finished and turn two
    /// started inside its ending, still holding its own gate and still having said nothing.
    /// </remarks>
    public void ReleaseTurn(int ordinal)
    {
        lock (_lock)
        {
            GateFor(ordinal).TrySetResult();
        }
    }

    /// <inheritdoc />
    public Task<TurnResult> RunTurnAsync(string userInput, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the arbiter only ever streams a turn.");

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingAsync(
        string userInput,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Task gate;
        lock (_lock)
        {
            _turnsRun.Add(userInput);
            gate = _allReleased ? Task.CompletedTask : GateFor(++_turnsStarted).Task;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        yield return new ChatResponseUpdate(ChatRole.Assistant, userInput);
        yield return new ChatResponseUpdate(ChatRole.Assistant, " done");
    }

    /// <inheritdoc />
    public bool Interrupt(
        string utteranceUntilInterrupt,
        TimeSpan durationUntilInterrupt,
        bool cutsRunningTurn = true)
    {
        LastHeardText = utteranceUntilInterrupt;
        LastPlayedDuration = durationUntilInterrupt;
        LastCutRunningTurn = cutsRunningTurn;
        return true;
    }

    /// <summary>Finds one turn's gate, creating it if the turn has not reached it yet.</summary>
    /// <param name="ordinal">The turn, in the order this port was asked to run them, from one.</param>
    /// <returns>The gate that turn waits on.</returns>
    /// <remarks>Callers hold <c>_lock</c>.</remarks>
    private TaskCompletionSource GateFor(int ordinal)
    {
        if (!_gates.TryGetValue(ordinal, out var gate))
        {
            gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates[ordinal] = gate;
        }

        return gate;
    }
}

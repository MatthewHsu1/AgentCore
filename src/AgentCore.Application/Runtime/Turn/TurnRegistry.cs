using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime.Turn;

// The turn running on one session, keyed by the session the framework hands providers —
// the MAF AgentSession.StateBag shape, but live: the bag serializes and a turn holds
// sessions and tools, so the table carries what the bag cannot. The loop files the turn
// under the session the run actually gets (a graph row re-creates its own per turn);
// providers read it back off the session their InvokingContext carries. Weak keys: an
// entry dies with its session, and each turn overwrites the last, so nothing here leaks
// or crosses turns.
internal static class TurnRegistry
{
    private static readonly ConditionalWeakTable<AgentSession, TurnInvocation> Turns = new();

    /// <summary>Files the turn running on one session.</summary>
    /// <param name="session">The session the run actually gets.</param>
    /// <param name="turn">What that run may see and cite.</param>
    internal static void Set(AgentSession session, TurnInvocation turn)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);

        Turns.Remove(session);
        Turns.Add(session, turn);
    }

    /// <summary>Reads the turn a run on one session belongs to, or null when it is not ours.</summary>
    /// <param name="session">The session the framework is about to run on.</param>
    internal static TurnInvocation? For(AgentSession? session)
        => session is not null && Turns.TryGetValue(session, out var turn) ? turn : null;
}

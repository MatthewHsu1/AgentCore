using AgentCore.Application.Runtime;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// Opens one ambient at a time, so a collector can be tested without standing up a whole call.
/// </summary>
internal static class TurnAmbientsTestScope
{
    /// <summary>Opens a source collector over this flow.</summary>
    internal static IDisposable WithSources(TurnSources sources)
        => TurnAmbients.Amend(ambients => ambients with { Sources = sources });

    /// <summary>Opens an outer tool call over this flow.</summary>
    internal static IDisposable WithOuterCall(string callId) => OuterToolCall.Enter(callId, out _);

    /// <summary>Opens the call's ambiguity holder over this flow, the way <c>CallSession</c> does.</summary>
    internal static IDisposable WithClarifications(Clarifications clarifications)
        => TurnAmbients.Amend(ambients => ambients with { Clarifications = clarifications });

    /// <summary>
    /// Opens a turn context whose <see cref="TurnContext.CarriesHistory"/> is <paramref name="carriesHistory"/>
    /// — true for a row 1 or 2 turn, false for a graph row's own participant invocation (K39).
    /// </summary>
    internal static IDisposable WithCarriesHistory(bool carriesHistory)
        => TurnAmbients.Amend(ambients => ambients with
        {
            Context = new TurnContext { Session = new StubSession(), CarriesHistory = carriesHistory },
        });

    private sealed class StubSession : AgentSession;
}

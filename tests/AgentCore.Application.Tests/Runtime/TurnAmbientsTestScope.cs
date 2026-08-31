using AgentCore.Application.Runtime;

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
    internal static IDisposable WithOuterCall(string callId) => OuterToolCall.Enter(callId);
}

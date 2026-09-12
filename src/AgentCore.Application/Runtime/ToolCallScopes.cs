using AgentCore.Application.Tools;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The <see cref="ToolCallScope"/> of the turn running on this flow of execution.
/// </summary>
internal static class ToolCallScopes
{
    /// <summary>The message <see cref="Current"/> throws with when no turn is open.</summary>
    internal const string NoTurnMessage =
        "A binding declared a ToolCallScope parameter, and no turn is open on this flow of execution. "
        + "A bound tool receives a ToolCallScope only while a turn runs through a CallSession. Run the "
        + "tool through a CallSession, or take the ToolCallScope parameter off the binding.";

    /// <summary>Builds the scope of the call and turn running on this flow.</summary>
    /// <returns>The scope.</returns>
    /// <exception cref="InvalidOperationException">
    /// No turn is open on this flow of execution. The message names the fault and says what opens one.
    /// </exception>
    internal static ToolCallScope Current()
    {
        var ambients = TurnAmbients.Current;

        if (ambients?.CallId is not { } callId || ambients.State is not { } state)
        {
            throw new InvalidOperationException(NoTurnMessage);
        }

        return new ToolCallScope(callId, state.TurnIndex, state.Stage);
    }
}

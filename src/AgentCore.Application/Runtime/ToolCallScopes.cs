using AgentCore.Application.Tools;

namespace AgentCore.Application.Runtime;

// The <see cref="ToolCallScope"/> of one turn, built from the invocation the call runs under.
internal static class ToolCallScopes
{
    /// <summary>The message the binder throws with when no turn reached the tool.</summary>
    internal const string NoTurnMessage =
        "A binding declared a ToolCallScope parameter, and no turn is open on this flow of execution. "
        + "A bound tool receives a ToolCallScope only while a turn runs through a CallSession. Run the "
        + "tool through a CallSession, or take the ToolCallScope parameter off the binding.";

    /// <summary>Builds the scope of the call and turn one invocation belongs to.</summary>
    /// <param name="invocation">The turn the tool call runs under.</param>
    /// <returns>The scope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="invocation"/> is <see langword="null"/>.</exception>
    internal static ToolCallScope From(TurnInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        return new ToolCallScope(invocation.CallId, invocation.TurnIndex, invocation.Stage, invocation.Workspace);
    }
}

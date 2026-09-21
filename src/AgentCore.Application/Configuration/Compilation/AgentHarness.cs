using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Compilation
{
    /// <summary>One agent's harness switches, fully resolved.</summary>
    /// <param name="Todos">Whether the agent gets the todo tools.</param>
    /// <param name="Mode">Whether the agent gets the mode tools.</param>
    /// <param name="Clock">Whether the agent is told the current date and time each turn.</param>
    /// <param name="BaseInstructions">Whether the framework's base instructions sit above the document's own.</param>
    public sealed record ResolvedHarness(bool Todos, bool Mode, bool Clock, bool BaseInstructions);

    /// <summary>
    /// Composes one agent's harness switches (<c>todos:</c>, <c>mode:</c>, <c>clock:</c>,
    /// <c>baseInstructions:</c>) from <c>agents.defaults</c> and the agent's own keys, key by key.
    /// The tool switches are off until named; the two posture switches are on until named, so an
    /// agent has to opt out of knowing the date or of the base instructions rather than opt in.
    /// </summary>
    public static class AgentHarness
    {
        /// <summary>Composes one agent's resolved harness switches.</summary>
        /// <param name="defaults">The <c>agents.defaults</c> section, or <see langword="null"/>.</param>
        /// <param name="agent">The agent to resolve.</param>
        public static ResolvedHarness Compose(AgentDefaults? defaults, AgentConfiguration agent)
        {
            ArgumentNullException.ThrowIfNull(agent);

            return new ResolvedHarness(
                agent.Todos ?? defaults?.Todos ?? false,
                agent.Mode ?? defaults?.Mode ?? false,
                agent.Clock ?? defaults?.Clock ?? true,
                agent.BaseInstructions ?? defaults?.BaseInstructions ?? true);
        }
    }
}

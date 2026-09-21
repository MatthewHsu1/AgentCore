using AgentCore.Application.Runtime;

namespace AgentCore.Application.Knowledge
{
    /// <summary>The one probe this turn won, and the facet it is about to drop.</summary>
    /// <param name="Clarifications">The conversation's ambiguity holder.</param>
    /// <param name="Probe">The latch this caller won. Every way out publishes or fails it.</param>
    /// <param name="Facet">The facet the probe drops, and charges.</param>
    internal sealed record ClaimedProbe(Clarifications Clarifications, Clarifications.Probe Probe, string Facet);
}

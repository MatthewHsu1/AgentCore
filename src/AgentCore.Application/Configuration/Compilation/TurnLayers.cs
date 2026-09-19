using AgentCore.Application.Evaluation;
using AgentCore.Application.Runtime;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>The two turn-disposition layers one entry puts on every agent a turn runs.</summary>
/// <param name="FallbackReply">The resolved line the caller hears when a turn fails.</param>
/// <param name="RefusalReply">The resolved line the caller hears when the agent refuses to answer.</param>
/// <param name="Moderation">The endpoint seam, or <see langword="null"/> to moderate nothing.</param>
/// <param name="SpokenBy">The agents whose reply the caller hears, or <see langword="null"/> for all.</param>
internal sealed record TurnLayers(
    string FallbackReply,
    string RefusalReply,
    PromptModerator? Moderation,
    IReadOnlySet<string>? SpokenBy)
{
    /// <summary>Puts the layers on one agent.</summary>
    /// <param name="agent">The compiled agent of one entry, or of one <c>policy:</c> stage.</param>
    /// <returns>The agent the turn loop runs.</returns>
    public AIAgent Apply(AIAgent agent)
    {
        AIAgent layered = new FallbackAgent(agent, FallbackReply, SpokenBy);

        return Moderation is null
            ? layered
            : new ModerationAgent(layered, Moderation, RefusalReply, ModerationAgent.DefaultTimeout);
    }
}

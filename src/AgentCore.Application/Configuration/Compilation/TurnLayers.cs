using AgentCore.Application.Evaluation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation
{
    /// <summary>The turn layers one entry puts on every agent a turn runs: the two disposition layers, and the seal outside them.</summary>
    /// <param name="FallbackReply">The resolved line the caller hears when a turn fails.</param>
    /// <param name="RefusalReply">The resolved line the caller hears when the agent refuses to answer.</param>
    /// <param name="Moderation">The endpoint seam, or <see langword="null"/> to moderate nothing.</param>
    /// <param name="OutputAgents">The agents whose reply the caller hears, or <see langword="null"/> for all.</param>
    /// <param name="History">Store 1, which the seal commits every turn to.</param>
    internal sealed record TurnLayers(
        string FallbackReply,
        string RefusalReply,
        PromptModerator? Moderation,
        IReadOnlySet<string>? OutputAgents,
        AgentCoreChatHistoryProvider History)
    {
        /// <summary>Puts the layers on one agent, outermost first: the seal, then moderation, then the fallback.</summary>
        /// <param name="agent">The compiled agent of one entry, or of one <c>policy:</c> stage.</param>
        /// <returns>The agent the turn loop runs.</returns>
        public AIAgent Apply(AIAgent agent)
        {
            AIAgentBuilder builder = new AIAgentBuilder(agent)
                .Use(inner => new ConversationTurnAgent(inner, History));

            if (Moderation is not null)
            {
                builder = builder.Use(inner => new ModerationAgent(inner, Moderation, RefusalReply, ModerationAgent.DefaultTimeout));
            }

            return builder
                .Use(inner => new FallbackAgent(inner, FallbackReply, OutputAgents))
                .Build();
        }
    }
}

using AgentCore.Application.Evaluation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Layers;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using AgentCore.Application.Runtime.Agents;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Configuration.Compilation
{
    /// <summary>The turn layers one entry puts on every agent a turn runs: the turn gate, moderation and the fallback, and the seal outside them.</summary>
    /// <param name="FallbackReply">The resolved line the caller hears when a turn fails.</param>
    /// <param name="RefusalReply">The resolved line the caller hears when the agent refuses to answer.</param>
    /// <param name="Moderation">The endpoint seam, or <see langword="null"/> to moderate nothing.</param>
    /// <param name="OutputAgents">The agents whose reply the caller hears, or <see langword="null"/> for all.</param>
    /// <param name="History">The conversation history, which the seal commits every turn to.</param>
    /// <param name="Hooks">The hooks of this compile; the turn gate sits between the seal and moderation when a hook overrides it.</param>
    internal sealed record TurnLayers(
        string FallbackReply,
        string RefusalReply,
        PromptModerator? Moderation,
        IReadOnlySet<string>? OutputAgents,
        AgentCoreChatHistoryProvider History,
        HookRuntime Hooks)
    {
        /// <summary>Puts the layers on one agent, outermost first: the seal, the turn gate, moderation, then the fallback.</summary>
        /// <param name="agent">The compiled agent of one entry, or of one <c>policy:</c> stage.</param>
        /// <returns>The agent the turn loop runs.</returns>
        public AIAgent Apply(AIAgent agent)
        {
            AIAgentBuilder builder = new AIAgentBuilder(agent)
                .Use(inner => new ConversationTurnAgent(inner, History));

            if (Hooks.Gates.Overrides(GatePoint.BeforeTurn))
            {
                builder = builder.Use(inner => new TurnGateAgent(inner, Hooks, RefusalReply));
            }

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

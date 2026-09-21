using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Compaction
{
    /// <summary>
    /// Builds the one fixed compaction every agent runs: cap old tool results, then summarise the
    /// rest, sized off the model's own context window (D7, D9). The trigger-taking overload lets a
    /// test size the same pair to fire on a handful of fixture messages.
    /// </summary>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.21.0.
    internal static class CompactionStrategyFactory
    {
        /// <summary>Builds the stages off the window fractions.</summary>
        /// <param name="summariser">The chat client the summary stage calls. This is the agent's own reply model.</param>
        /// <param name="contextWindow">The reply model's context window, in tokens.</param>
        /// <returns>A <see cref="ToolResultCapProvider"/> and a <see cref="SummarizationCompactionStrategy"/>, on the same trigger.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="contextWindow"/> is not positive.</exception>
        public static CompactionStages Create(IChatClient summariser, int contextWindow)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(contextWindow, 0);

            return Create(
                summariser,
                CompactionTriggers.TokensExceed((int)(contextWindow * CompactionDefaults.FireFraction)),
                CompactionTriggers.TokensBelow((int)(contextWindow * CompactionDefaults.TargetFraction)));
        }

        /// <summary>Builds the stages off explicit triggers. A test passes <c>Always</c> and <c>Never</c> here.</summary>
        /// <param name="summariser">The chat client the summary stage calls.</param>
        /// <param name="fire">When a stage applies, judged over everything bound for the model.</param>
        /// <param name="target">Where the summary stops. Only the summary stage reads this.</param>
        /// <returns>A <see cref="ToolResultCapProvider"/> and a <see cref="SummarizationCompactionStrategy"/>, on the same trigger.</returns>
        public static CompactionStages Create(IChatClient summariser, CompactionTrigger fire, CompactionTrigger target)
        {
            ArgumentNullException.ThrowIfNull(summariser);
            ArgumentNullException.ThrowIfNull(fire);
            ArgumentNullException.ThrowIfNull(target);

            return new CompactionStages(
                new ToolResultCapProvider(fire, CompactionDefaults.Keep, CompactionDefaults.CapResultChars),
                new SummarizationCompactionStrategy(summariser, fire, CompactionDefaults.Keep, target: target));
        }

        /// <summary>Builds the providers in chain order: the cap always runs, the summary needs history to persist its row.</summary>
        /// <param name="stages">The cap provider and the strategy the summary provider runs.</param>
        /// <param name="history">Store 1, or <see langword="null"/> when the session carries no history.</param>
        /// <returns>The cap, then the summary when there is somewhere to write it.</returns>
        public static AIContextProvider[] BuildProviders(CompactionStages stages, AgentCoreChatHistoryProvider? history)
        {
            ArgumentNullException.ThrowIfNull(stages);

            return history is null
                ? [stages.Cap]
                : [stages.Cap, new SummaryRowProvider(history, stages.Summary)];
        }
    }
#pragma warning restore MAAI001
}

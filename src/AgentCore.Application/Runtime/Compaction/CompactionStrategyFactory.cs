using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Compaction
{
    /// <summary>
    /// Builds the one fixed compaction every agent runs.
    /// </summary>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.21.0.
    internal static class CompactionStrategyFactory
    {
        /// <summary>Builds the stages off the window fraction and the token ceiling.</summary>
        /// <param name="summariser">The chat client the summary stage calls. This is the agent's own reply model.</param>
        /// <param name="contextWindow">The reply model's context window, in tokens.</param>
        /// <returns>A <see cref="ToolResultCapProvider"/> and a <see cref="SummarizationCompactionStrategy"/>, on the same trigger.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="contextWindow"/> is not positive.</exception>
        public static CompactionStages Create(IChatClient summariser, int contextWindow)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(contextWindow, 0);

            return Create(summariser, CompactionTriggers.TokensExceed(CompactionDefaults.FireTokens(contextWindow)));
        }

        /// <summary>Builds the stages off an explicit trigger. A test passes <c>Always</c> or <c>Never</c> here.</summary>
        /// <param name="summariser">The chat client the summary stage calls.</param>
        /// <param name="fire">When a stage applies, judged over everything bound for the model.</param>
        /// <returns>A <see cref="ToolResultCapProvider"/> and a <see cref="SummarizationCompactionStrategy"/> factory, on the same trigger.</returns>
        public static CompactionStages Create(IChatClient summariser, CompactionTrigger fire)
        {
            ArgumentNullException.ThrowIfNull(summariser);
            ArgumentNullException.ThrowIfNull(fire);

            return new CompactionStages(
                new ToolResultCapProvider(fire, CompactionDefaults.Keep, CompactionDefaults.CapResultChars),
                summariser,
                client => new SummarizationCompactionStrategy(client, fire, CompactionDefaults.Keep, target: CompactionTriggers.Never));
        }

        /// <summary>Builds the providers in chain order: the cap always runs, the summary needs history to persist its row.</summary>
        /// <param name="stages">The cap provider and what the summary provider builds its strategy from.</param>
        /// <param name="history">Store 1, or <see langword="null"/> when the session carries no history.</param>
        /// <returns>The cap, then the summary when there is somewhere to write it.</returns>
        public static AIContextProvider[] BuildProviders(CompactionStages stages, AgentCoreChatHistoryProvider? history)
        {
            ArgumentNullException.ThrowIfNull(stages);

            return history is null
                ? [stages.Cap]
                : [stages.Cap, new SummaryRowProvider(history, stages.Summariser, stages.Summary)];
        }
    }
#pragma warning restore MAAI001
}

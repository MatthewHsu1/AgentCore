using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace AgentCore.Application.Evaluation
{
    /// <summary>
    /// Reads which moderation categories flagged what the caller said.
    /// </summary>
    public sealed class PromptModerator
    {
        /// <summary>The name the composition root registers the moderation evaluator under.</summary>
        public const string ModerationEvaluatorName = "moderation";

        private static readonly IReadOnlyList<string> NothingFlagged = [];

        private readonly IEvaluator _evaluator;

        /// <summary>Builds a moderator over one evaluator.</summary>
        /// <param name="evaluator">The evaluator that reaches the moderation endpoint.</param>
        /// <exception cref="ArgumentNullException">The evaluator is <see langword="null"/>.</exception>
        public PromptModerator(IEvaluator evaluator)
        {
            ArgumentNullException.ThrowIfNull(evaluator);
            _evaluator = evaluator;
        }

        /// <summary>Builds a moderator over the evaluator the host registered, or none.</summary>
        /// <param name="registry">The registry the composition root filled.</param>
        /// <returns>
        /// The moderator, or <see langword="null"/> when the registry holds no evaluator under
        /// <see cref="ModerationEvaluatorName"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">The registry is <see langword="null"/>.</exception>
        public static PromptModerator? FromRegistry(EvaluatorRegistry registry)
        {
            ArgumentNullException.ThrowIfNull(registry);

            return registry.TryGetEvaluator(ModerationEvaluatorName, out IEvaluator? evaluator) && evaluator is not null
                ? new PromptModerator(evaluator)
                : null;
        }

        /// <summary>Reads which categories flagged what the caller said.</summary>
        /// <param name="callerText">The words the caller spoke this turn.</param>
        /// <param name="cancellationToken">Cancels the endpoint conversation.</param>
        /// <returns>
        /// The categories, in the order the endpoint returned them, or an empty list when the endpoint
        /// flagged nothing.
        /// </returns>
        /// <exception cref="ArgumentNullException">The text is <see langword="null"/>.</exception>
        public async ValueTask<IReadOnlyList<string>> FlaggedCategoriesAsync(
            string callerText,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(callerText);

            // The IEvaluator signature carries the text under test on the response, so the caller's words
            // ride there. The evaluator moderates whatever text it is given and never asks who said it.
            ChatResponse spoken = new(new ChatMessage(ChatRole.User, callerText));

            EvaluationResult result = await _evaluator
                .EvaluateAsync([], spoken, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return ModerationVerdict.TryRead(result, out ModerationVerdict? verdict) && verdict is { Flagged: true }
                ? verdict.Categories
                : NothingFlagged;
        }
    }
}

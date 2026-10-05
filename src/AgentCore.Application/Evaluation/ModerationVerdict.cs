using Microsoft.Extensions.AI.Evaluation;

namespace AgentCore.Application.Evaluation
{
    /// <summary>
    /// What the moderation endpoint answered about one piece of text.
    /// </summary>
    public sealed class ModerationVerdict : EvaluationContext
    {
        /// <summary>The name this verdict carries on the metric.</summary>
        public const string ModerationVerdictName = "Moderation Verdict";

        /// <summary>The text a verdict with no flagged category carries as its content.</summary>
        public const string NothingFlagged = "the endpoint flagged no category.";

        /// <summary>Builds a verdict from what the endpoint answered.</summary>
        /// <param name="flagged">Whether the endpoint flagged the text.</param>
        /// <param name="categories">
        /// The categories the endpoint flagged, in the order it returned them. It is empty when the
        /// endpoint flagged nothing.
        /// </param>
        /// <exception cref="ArgumentNullException">The categories are <see langword="null"/>.</exception>
        public ModerationVerdict(bool flagged, IEnumerable<string> categories)
            : this(flagged, Materialize(categories))
        {
        }

        private ModerationVerdict(bool flagged, string[] categories)
            : base(ModerationVerdictName, categories.Length == 0 ? NothingFlagged : string.Join(", ", categories))
        {
            Flagged = flagged;
            Categories = categories;
        }

        /// <summary>Gets whether the endpoint flagged the text.</summary>
        public bool Flagged { get; }

        /// <summary>Gets the categories the endpoint flagged, in the order it returned them.</summary>
        public IReadOnlyList<string> Categories { get; }

        /// <summary>Reads the verdict one evaluation carries.</summary>
        /// <param name="result">What the moderation evaluator returned.</param>
        /// <param name="verdict">The verdict, when one metric carries it.</param>
        /// <returns><see langword="true"/> when the endpoint answered.</returns>
        /// <exception cref="ArgumentNullException">The result is <see langword="null"/>.</exception>
        public static bool TryRead(EvaluationResult result, out ModerationVerdict? verdict)
        {
            ArgumentNullException.ThrowIfNull(result);

            foreach (EvaluationMetric metric in result.Metrics.Values)
            {
                if (metric.Context?.TryGetValue(ModerationVerdictName, out EvaluationContext? context) == true
                    && context is ModerationVerdict found)
                {
                    verdict = found;
                    return true;
                }
            }

            verdict = null;
            return false;
        }

        private static string[] Materialize(IEnumerable<string> categories)
        {
            ArgumentNullException.ThrowIfNull(categories);
            return [.. categories];
        }
    }
}

using AgentCore.Application.Transcript;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Compaction
{
    /// <summary>
    /// Reads a <see cref="CompactionStrategy"/>'s output as a shape one summary row can stand for.
    /// </summary>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.21.0.
    internal static class SummaryRowShape
    {
        /// <summary>Whether the floor's rows lead the model-bound list, id for id.</summary>
        internal static bool Leads(IReadOnlyList<ViewMessage> floor, IReadOnlyList<ChatMessage> messages)
        {
            if (floor.Count > messages.Count)
            {
                return false;
            }

            for (int index = 0; index < floor.Count; index++)
            {
                if (!string.Equals(floor[index].Message.MessageId, messages[index].MessageId, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Reads the strategy's output as one new message over a prefix of the view, and names where the
        /// kept tail starts. The output must be one new message, then the view from that index on, the
        /// very instances in order; anything else is not a shape one summary row can stand for.
        /// </summary>
        /// <returns>The index of the first kept message, or <see langword="null"/>. The view's count when nothing was kept.</returns>
        internal static int? Kept(List<ChatMessage> view, List<ChatMessage> output)
        {
            if (output.Count == 0 || output.Count > view.Count || view.Any(message => ReferenceEquals(message, output[0])))
            {
                return null;
            }

            int kept = view.Count - (output.Count - 1);
            for (int index = 1; index < output.Count; index++)
            {
                if (!ReferenceEquals(output[index], view[kept + index - 1]))
                {
                    return null;
                }
            }

            return kept;
        }
    }
#pragma warning restore MAAI001
}

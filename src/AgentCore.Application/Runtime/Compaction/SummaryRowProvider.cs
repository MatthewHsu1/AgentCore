using AgentCore.Application.Diagnostics;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Runtime.Compaction
{
    /// <summary>
    /// The persisted half of compaction. Runs in the provider chain after the tool-result cap, over the
    /// rows before the newest turn, and hands the model the strategy's output: one summary message, then
    /// the rows it kept. The summary lands in the transcript as a row that covers the rows it replaced
    /// (<see cref="AgentCoreChatHistoryProvider.Compact"/>) before the request goes out, so the next
    /// session opens on it and the summariser is never asked about those rows again.
    /// </summary>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.21.0.
    internal sealed class SummaryRowProvider : AIContextProvider
    {
        private readonly AgentCoreChatHistoryProvider _history;

        private readonly CompactionStrategy _summary;

        /// <param name="history">Store 1, which says what a compaction may cover and takes the row.</param>
        /// <param name="summary">The strategy that replaces the oldest messages with one summary. One model call.</param>
        public SummaryRowProvider(AgentCoreChatHistoryProvider history, CompactionStrategy summary)
        {
            ArgumentNullException.ThrowIfNull(history);
            ArgumentNullException.ThrowIfNull(summary);

            _history = history;
            _summary = summary;
        }

        /// <inheritdoc />
        protected override async ValueTask<AIContext> InvokingCoreAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            AIContext input = context.AIContext;
            if (input.Messages is null || context.Session is not { } session || _history.Floor(session) is not { } floor)
            {
                return input;
            }

            IReadOnlyList<ChatMessage> messages = input.Messages as IReadOnlyList<ChatMessage> ?? [.. input.Messages];
            if (!Leads(floor.Messages, messages))
            {
                return input;
            }

            TurnInvocation? turn = TurnRegistry.For(session);
            ILogger logger = turn?.Logger ?? NullLogger.Instance;

            // The stages before this one strip and cap by making new instances, so the view the
            // strategy runs over is the model-bound prefix, not the floor's own messages; the floor
            // lines up with it by index and lends it the ordinals.
            List<ChatMessage> view = [.. messages.Take(floor.Messages.Count)];

            try
            {
                List<ChatMessage> output = [.. await CompactionProvider.CompactAsync(_summary, view, logger, cancellationToken).ConfigureAwait(false)];

                if (output.Count == view.Count && output.Zip(view).All(pair => ReferenceEquals(pair.First, pair.Second)))
                {
                    return input;
                }

                if (Kept(view, output) is not { } kept)
                {
                    if (turn is not null)
                    {
                        Log.TranscriptCompactionUnsupported(logger, turn.ConversationId, turn.TurnIndex);
                    }

                    return input;
                }

                int coversUpTo = floor.Messages[kept - 1].LastOrdinal;
                return coversUpTo <= floor.CoversUpTo || !_history.Compact(session, output[0], coversUpTo, floor.Revision)
                    ? input
                    : new AIContext
                    {
                        Instructions = input.Instructions,
                        Messages = [output[0], .. messages.Skip(kept)],
                        Tools = input.Tools,
                    };
            }
#pragma warning disable CA1031 // A strategy that throws never ends a conversation: the session keeps the view it had.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                if (turn is not null)
                {
                    Log.TranscriptCompactionFailed(logger, turn.ConversationId, turn.TurnIndex, exception);
                }

                return input;
            }
        }

        /// <summary>Whether the floor's rows lead the model-bound list, id for id.</summary>
        private static bool Leads(IReadOnlyList<ViewMessage> floor, IReadOnlyList<ChatMessage> messages)
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
        private static int? Kept(List<ChatMessage> view, List<ChatMessage> output)
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

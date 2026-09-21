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

        private readonly IChatClient _summariser;

        private readonly Func<IChatClient, CompactionStrategy> _summary;

        /// <param name="history">Store 1, which says what a compaction may cover and takes the row.</param>
        /// <param name="summariser">The chat client the strategy calls. Wrapped fresh each invocation, so the notice tracks the call it actually makes.</param>
        /// <param name="summary">Builds the strategy that replaces the oldest messages with one summary, over the client it is handed.</param>
        public SummaryRowProvider(AgentCoreChatHistoryProvider history, IChatClient summariser, Func<IChatClient, CompactionStrategy> summary)
        {
            ArgumentNullException.ThrowIfNull(history);
            ArgumentNullException.ThrowIfNull(summariser);
            ArgumentNullException.ThrowIfNull(summary);

            _history = history;
            _summariser = summariser;
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
            if (!SummaryRowShape.Leads(floor.Messages, messages))
            {
                return input;
            }

            TurnInvocation? turn = TurnRegistry.For(session);
            ILogger logger = turn?.Logger ?? NullLogger.Instance;

            List<ChatMessage> view = [.. messages.Take(floor.Messages.Count)];

            NoticingChatClient client = new(_summariser, turn?.Notices);
            CompactionStrategy strategy = _summary(client);

            try
            {
                List<ChatMessage> output = [.. await CompactionProvider.CompactAsync(strategy, view, logger, cancellationToken).ConfigureAwait(false)];

                if (output.Count == view.Count && output.Zip(view).All(pair => ReferenceEquals(pair.First, pair.Second)))
                {
                    PostEnd(turn, client, "unchanged");
                    return input;
                }

                if (SummaryRowShape.Kept(view, output) is not { } kept)
                {
                    if (turn is not null)
                    {
                        Log.TranscriptCompactionUnsupported(logger, turn.ConversationId, turn.TurnIndex);
                    }

                    PostEnd(turn, client, "unchanged");
                    return input;
                }

                int coversUpTo = floor.Messages[kept - 1].LastOrdinal;
                if (coversUpTo <= floor.CoversUpTo || !_history.Compact(session, output[0], coversUpTo, floor.Revision))
                {
                    PostEnd(turn, client, "unchanged");
                    return input;
                }

                PostEnd(turn, client, "compacted");
                return new AIContext
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

                PostEnd(turn, client, "failed");
                return input;
            }
        }

        /// <summary>Posts the end notice, but only when the summariser was actually called: no call, no flicker.</summary>
        private static void PostEnd(TurnInvocation? turn, NoticingChatClient client, string outcome)
        {
            if (client.Called)
            {
                turn?.Notices?.Post(new CompactionContent(CompactionContent.EndPhase, outcome));
            }
        }
    }
#pragma warning restore MAAI001
}

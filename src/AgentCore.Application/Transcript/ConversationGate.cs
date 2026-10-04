using AgentCore.Application.Conversation;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Transcript
{
    /// <summary>
    /// What one conversation holds outside its state bag: its lock, its transcript, and its queue of
    /// store writes, which never faults and lets the conversation outlive a store that refuses.
    /// </summary>
    /// <param name="store">The message store, where every queued write lands.</param>
    /// <param name="logger">Where a refused write is logged.</param>
    internal sealed class ConversationGate(IConversationStore store, ILogger logger)
    {
        private readonly IConversationStore _store = store;

        private readonly ILogger _logger = logger;

        /// <summary>Gets the lock every read and every change of this conversation's transcript takes.</summary>
        public Lock Sync { get; } = new();

        /// <summary>Gets the conversation's live transcript. It never enters the state bag, so serializing the bag stays small.</summary>
        public ConversationTranscript Transcript { get; } = new();

        /// <summary>Gets the tail of this conversation's store writes. It never faults.</summary>
        public Task Writes { get; private set; } = Task.CompletedTask;

        /// <summary>Gets or sets where a dropped write of this conversation is counted, if anywhere.</summary>
        public ITranscriptLossCounter? Losses { get; set; }

        /// <summary>
        /// Gets what the framework handed the provider for the running turn and nothing has committed yet.
        /// It grows once per inner run, because a <c>loop:</c> entry and an approval re-entry run the agent
        /// several times in one turn.
        /// </summary>
        public List<ChatMessage> Staged { get; } = [];

        /// <summary>
        /// Gets what the running turn's finished runs were told and answered, in order. A later run of the same turn
        /// reads it after the conversation, since the framework hands the caller's input to the first run only. It is
        /// never committed: the turn's user row and <see cref="Staged"/> are. The next turn's start clears it.
        /// </summary>
        public List<ChatMessage> Replay { get; } = [];

        /// <summary>Takes everything staged for the running turn, and leaves nothing staged.</summary>
        public List<ChatMessage> TakeStaged()
        {
            List<ChatMessage> staged = [.. Staged];
            Staged.Clear();
            return staged;
        }

        /// <summary>Queues the append of rows the transcript just took, with the state that rides their batch.</summary>
        /// <returns>
        /// Completes once the append landed or was dropped: <see langword="true"/> when the store refused it because the
        /// conversation already saved the turn it names.
        /// </returns>
        public Task<bool> QueueAppend(IReadOnlyList<ConversationMessage> rows, ConversationSessionState? state)
        {
            ConversationMessageDraft[] drafts = [.. rows.Select(row => new ConversationMessageDraft(row.TurnIndex, row.Content, row.MessageId) { CoversUpTo = row.CoversUpTo })];
            string conversationId = Transcript.ConversationId;
            return Enqueue(
                () => new ValueTask(_store.AppendAsync(conversationId, drafts, state, CancellationToken.None).AsTask()),
                [.. drafts.Select(draft => draft.MessageId)]);
        }

        /// <summary>Queues the write of a state no rows carry, behind every write already queued.</summary>
        public void QueueState(ConversationSessionState state)
        {
            string conversationId = Transcript.ConversationId;
            _ = Enqueue(() => _store.SaveStateAsync(conversationId, state, CancellationToken.None));
        }

        /// <summary>Queues the rewrite of rows the transcript just changed in place.</summary>
        public void QueueRewrite(IReadOnlyList<ConversationMessage> rows)
        {
            foreach (ConversationMessage row in rows)
            {
                _ = Enqueue(() => _store.RewriteAsync(row.ConversationId, row.MessageId, row.Content, CancellationToken.None));
            }
        }

        /// <summary>Queues the delete of rows the transcript just dropped.</summary>
        public void QueueRemove(IReadOnlyList<ConversationMessage> rows)
        {
            foreach (ConversationMessage row in rows)
            {
                _ = Enqueue(() =>
                {
                    Losses?.Withdrew([row.MessageId]);
                    return _store.DeleteMessageAsync(row.ConversationId, row.MessageId, CancellationToken.None);
                });
            }
        }

        /// <summary>Queues the delete of every row from one ordinal on.</summary>
        /// <param name="fromOrdinal">The first ordinal to delete.</param>
        /// <param name="withdrawn">The ids of the rows the transcript just dropped for it.</param>
        public void QueueTruncate(int fromOrdinal, IReadOnlyList<string> withdrawn)
        {
            string conversationId = Transcript.ConversationId;
            _ = Enqueue(() =>
            {
                Losses?.Withdrew(withdrawn);
                return new ValueTask(_store.TruncateAsync(conversationId, fromOrdinal, CancellationToken.None).AsTask());
            });
        }

        /// <summary>Queues one store write behind everything this conversation has already queued. Runs under <see cref="Sync"/>.</summary>
        /// <param name="write">The write.</param>
        /// <param name="appended">The ids of the rows the write appends, or none for a write that changes rows in place.</param>
        /// <returns>Completes once the write landed or was dropped: <see langword="true"/> when the store refused a turn it already saved.</returns>
        public Task<bool> Enqueue(Func<ValueTask> write, IReadOnlyList<string>? appended = null)
        {
            Task<bool> written = WriteAfterAsync(Writes, write, Transcript.ConversationId, Transcript.TurnIndex, appended ?? []);
            Writes = written;
            return written;
        }

        /// <summary>Writes to the store, and lets the conversation outlive a store that refuses.</summary>
        private async Task<bool> WriteAfterAsync(
            Task previous, Func<ValueTask> write, string conversationId, int turnIndex, IReadOnlyList<string> appended)
        {
            await previous.ConfigureAwait(false);

            try
            {
                await write().ConfigureAwait(false);
                return false;
            }
#pragma warning disable CA1031 // A message store write failure never ends a conversation, and never breaks the chain behind it.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                if (Losses is null)
                {
                    Log.TranscriptWriteFailed(_logger, conversationId, turnIndex, exception);
                }

                Losses?.Dropped(turnIndex, appended, exception);
                return exception is ConversationTurnConflictException;
            }
        }
    }
}

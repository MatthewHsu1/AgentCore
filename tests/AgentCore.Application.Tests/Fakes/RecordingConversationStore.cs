using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.TestSupport;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes
{
    /// <summary>A store kept in this process, with the conversations made against its words recorded.</summary>
    internal sealed class RecordingConversationStore() : DelegatingConversationStore(new InMemoryConversationStore())
    {
        private readonly Lock _lock = new();

        /// <summary>The words as the store holds them now. It backs <see cref="Live"/>, not <see cref="Rows"/>.</summary>
        private readonly Dictionary<(string ConversationId, int Ordinal), ConversationMessage> _rows = [];

        /// <summary>Gets every row the provider appended, in the order it appended them.</summary>
        public List<ConversationMessage> Rows { get; } = [];

        /// <summary>Gets how many appends reached the store. One append is one batch, however many rows it holds.</summary>
        public int Appends { get; private set; }

        /// <summary>Gets the state each append carried, one entry per append, in order.</summary>
        public List<ConversationSessionState?> States { get; } = [];

        /// <summary>Gets or sets a log that every append writes <c>append</c> to, for a test that orders it against other steps.</summary>
        public List<string>? Log { get; set; }

        /// <summary>Gets every rewrite the provider asked for, in order.</summary>
        public List<ConversationMessage> Rewrites { get; } = [];

        /// <summary>Gets how many times a conversation was read back, by a consumer or by its session.</summary>
        public int Reads { get; private set; }

        /// <summary>Reads one conversation as the store holds it now, oldest message first.</summary>
        /// <param name="conversationId">The conversation to read.</param>
        public IReadOnlyList<ConversationMessage> Live(string conversationId)
        {
            lock (_lock)
            {
                return [.. _rows.Values.Where(row => row.ConversationId == conversationId).OrderBy(row => row.Ordinal)];
            }
        }

        /// <inheritdoc />
        public override async ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
            string conversationId,
            IReadOnlyList<ConversationMessageDraft> messages,
            ConversationSessionState? state = null,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ConversationMessage> rows = await base.AppendAsync(conversationId, messages, state, cancellationToken).ConfigureAwait(false);

            lock (_lock)
            {
                Appends++;
                States.Add(state);
                Log?.Add("append");
                Rows.AddRange(rows);
                foreach (ConversationMessage row in rows)
                {
                    _rows[(row.ConversationId, row.Ordinal)] = row;
                }
            }

            return rows;
        }

        /// <inheritdoc />
        public override async ValueTask RewriteAsync(
            string conversationId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
        {
            await base.RewriteAsync(conversationId, messageId, content, cancellationToken).ConfigureAwait(false);

            lock (_lock)
            {
                foreach (KeyValuePair<(string ConversationId, int Ordinal), ConversationMessage> pair in _rows)
                {
                    if (pair.Key.ConversationId != conversationId || pair.Value.MessageId != messageId)
                    {
                        continue;
                    }

                    ConversationMessage rewritten = pair.Value with { Content = content };
                    _rows[pair.Key] = rewritten;
                    Rewrites.Add(rewritten);
                    break;
                }
            }
        }

        /// <inheritdoc />
        public override async ValueTask DeleteMessageAsync(
            string conversationId, string messageId, CancellationToken cancellationToken = default)
        {
            await base.DeleteMessageAsync(conversationId, messageId, cancellationToken).ConfigureAwait(false);

            lock (_lock)
            {
                foreach ((string ConversationId, int Ordinal) key in _rows.Keys)
                {
                    if (key.ConversationId == conversationId && _rows[key].MessageId == messageId)
                    {
                        _ = _rows.Remove(key);
                        break;
                    }
                }
            }
        }

        /// <inheritdoc />
        /// <remarks>The rows are the ones somebody said: nothing here writes a summary row.</remarks>
        public override ValueTask<IReadOnlyList<ConversationMessage>> ReadForSessionAsync(
            string conversationId, CancellationToken cancellationToken = default)
        {
            return Read(conversationId);
        }

        private ValueTask<IReadOnlyList<ConversationMessage>> Read(string conversationId)
        {
            lock (_lock)
            {
                Reads++;
            }

            return ValueTask.FromResult(Live(conversationId));
        }

        /// <inheritdoc />
        public override ValueTask<int> EraseAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                List<(string ConversationId, int Ordinal)> going = [.. _rows.Keys.Where(key => key.ConversationId == conversationId)];
                foreach ((string ConversationId, int Ordinal) key in going)
                {
                    _ = _rows.Remove(key);
                }

                return ValueTask.FromResult(going.Count);
            }
        }
    }
}

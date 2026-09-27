using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Conversation.Memory
{
    /// <summary>The store backing that keeps every row in this process: a conversation, and its words.</summary>
    /// <remarks>Creates the store.</remarks>
    /// <param name="timeProvider">Where <c>created_at</c> comes from, or <see langword="null"/> for the system clock.</param>
    public sealed class InMemoryConversationStore(TimeProvider? timeProvider = null) : IConversationStore
    {
        private readonly Lock _lock = new();

        private readonly Dictionary<string, ConversationRecord> _conversations = [];

        private readonly InMemoryConversationWords _words = new();

        /// <summary>The resume blob of each conversation, beside the row rather than on it.</summary>
        private readonly Dictionary<string, ConversationSessionState> _state = [];

        private readonly InMemoryConversationClaims _claims = new();

        private readonly InMemoryConversationContinuations _continuations = new();

        private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

        private readonly InMemoryConversationBusyMarks _busy = new(timeProvider ?? TimeProvider.System);

        /// <inheritdoc />
        public ValueTask<ConversationRecord> CreateAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            lock (_lock)
            {
                if (!_conversations.TryGetValue(conversationId, out ConversationRecord? existing))
                {
                    DateTimeOffset now = _time.GetUtcNow();
                    existing = new ConversationRecord(conversationId, null, ConversationStatus.Regular, null, null, now, null);
                    _conversations[conversationId] = existing;
                }

                return ValueTask.FromResult(existing with
                {
                    State = _state.GetValueOrDefault(conversationId),
                    NextOrdinal = _words.NextOrdinal(conversationId),
                });
            }
        }

        /// <inheritdoc />
        public ValueTask<ConversationRecord?> GetAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            lock (_lock)
            {
                ConversationRecord? conversation = _conversations.GetValueOrDefault(conversationId);

                return ValueTask.FromResult(conversation is null
                    ? null
                    : conversation with
                    {
                        State = _state.GetValueOrDefault(conversationId),
                        NextOrdinal = _words.NextOrdinal(conversationId),
                    });
            }
        }

        /// <inheritdoc />
        public ValueTask<ConversationPage> ListAsync(
            string principalKey,
            string? after,
            int limit,
            ConversationStatus? status = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(principalKey);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

            lock (_lock)
            {
                IEnumerable<ConversationRecord> claimed = _claims
                    .ConversationsOf(principalKey)
                    .Select(_conversations.GetValueOrDefault)
                    .OfType<ConversationRecord>();

                return ValueTask.FromResult(InMemoryConversationListing.Page(claimed, after, limit, status));
            }
        }

        /// <inheritdoc />
        public ValueTask RenameAsync(string conversationId, string title, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);
            ArgumentNullException.ThrowIfNull(title);

            return Amend(conversationId, conversation => conversation with { Title = title });
        }

        /// <inheritdoc />
        public ValueTask SetStatusAsync(string conversationId, ConversationStatus status, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            return Amend(conversationId, conversation => conversation with { Status = status });
        }

        /// <inheritdoc />
        public ValueTask SetCustomAsync(
            string conversationId, JsonElement? custom, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            return Amend(conversationId, conversation => conversation with { Custom = custom?.Clone() });
        }

        /// <inheritdoc />
        public ValueTask SetExternalIdAsync(
            string conversationId, string? externalId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            return Amend(conversationId, conversation => conversation with { ExternalId = externalId });
        }

        /// <inheritdoc />
        public ValueTask DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            lock (_lock)
            {
                Forget(conversationId);
            }

            return default;
        }

        /// <inheritdoc />
        public ValueTask<int> SweepAsync(
            TimeSpan retention, int batchSize = 500, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(retention, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

            lock (_lock)
            {
                DateTimeOffset cutoff = _time.GetUtcNow() - retention;

                return ValueTask.FromResult(_continuations.Sweep(cutoff));
            }
        }

        /// <inheritdoc />
        public ValueTask<bool> TryMarkBusyAsync(
            string conversationId, string holder, TimeSpan lease, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(_busy.TryMark(conversationId, holder, lease));
        }

        /// <inheritdoc />
        public ValueTask ClearBusyAsync(string conversationId, string holder, CancellationToken cancellationToken = default)
        {
            _busy.Clear(conversationId, holder);
            return default;
        }

        /// <inheritdoc />
        public ValueTask AttachPrincipalAsync(
            string conversationId, string principalKey, string role, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);
            ArgumentNullException.ThrowIfNull(principalKey);
            ArgumentNullException.ThrowIfNull(role);

            lock (_lock)
            {
                _claims.Attach(conversationId, principalKey);
            }

            return default;
        }

        /// <inheritdoc />
        public ValueTask DetachPrincipalAsync(
            string conversationId, string principalKey, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);
            ArgumentNullException.ThrowIfNull(principalKey);

            lock (_lock)
            {
                _claims.Detach(conversationId, principalKey);
            }

            return default;
        }

        /// <inheritdoc />
        public ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
            string conversationId,
            IReadOnlyList<ConversationMessageDraft> messages,
            ConversationSessionState? state = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);
            ArgumentNullException.ThrowIfNull(messages);

            if (messages.Count == 0)
            {
                return ValueTask.FromResult<IReadOnlyList<ConversationMessage>>([]);
            }

            lock (_lock)
            {
                if (!_conversations.TryGetValue(conversationId, out ConversationRecord? conversation))
                {
                    throw new InvalidOperationException($"Store 0 holds no conversation '{conversationId}' to append words to.");
                }

                int fallbackTurnIndex = _state.GetValueOrDefault(conversationId)?.NextTurnIndex ?? 0;
                if (messages.Min(message => message.TurnIndex) is { } named && named < fallbackTurnIndex)
                {
                    throw new ConversationTurnConflictException(
                        $"Conversation '{conversationId}' already saved turn {named} and takes turn {fallbackTurnIndex} next, so nothing was written.");
                }

                IReadOnlyList<ConversationMessage> rows = _words.Append(conversationId, messages, fallbackTurnIndex);

                DateTimeOffset now = _time.GetUtcNow();
                _conversations[conversationId] = conversation with { LastMessageAt = now };

                if (state is not null && state.NextTurnIndex >= fallbackTurnIndex)
                {
                    _state[conversationId] = state;
                }

                return ValueTask.FromResult(rows);
            }
        }

        /// <inheritdoc />
        public ValueTask RewriteAsync(
            string conversationId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);
            ArgumentException.ThrowIfNullOrEmpty(messageId);
            ArgumentNullException.ThrowIfNull(content);

            lock (_lock)
            {
                _words.Rewrite(conversationId, messageId, content);
            }

            return default;
        }

        /// <inheritdoc />
        public ValueTask DeleteMessageAsync(string conversationId, string messageId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);
            ArgumentException.ThrowIfNullOrEmpty(messageId);

            lock (_lock)
            {
                _words.Delete(conversationId, messageId);
            }

            return default;
        }

        /// <inheritdoc />
        public ValueTask<IReadOnlyList<ConversationMessage>> ReadForSessionAsync(
            string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            lock (_lock)
            {
                return ValueTask.FromResult(_words.ReadForSession(conversationId));
            }
        }

        /// <inheritdoc />
        public ValueTask<IReadOnlyList<ConversationMessage>> ReadWindowAsync(
            string conversationId, TranscriptWindow window, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            lock (_lock)
            {
                return ValueTask.FromResult(_words.Read(conversationId, window));
            }
        }

        /// <inheritdoc />
        public ValueTask<int?> OrdinalOfAsync(
            string conversationId, string messageId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);
            ArgumentException.ThrowIfNullOrEmpty(messageId);

            lock (_lock)
            {
                return ValueTask.FromResult(_words.OrdinalOf(conversationId, messageId));
            }
        }

        /// <inheritdoc />
        public ValueTask<ConversationCut> TruncateAsync(
            string conversationId, int fromOrdinal, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            lock (_lock)
            {
                return ValueTask.FromResult(_words.Truncate(conversationId, fromOrdinal));
            }
        }

        /// <inheritdoc />
        public ValueTask<int> EraseAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            lock (_lock)
            {
                return ValueTask.FromResult(_words.Erase(conversationId));
            }
        }

        /// <inheritdoc />
        public ValueTask SaveContinuationAsync(string responseId, string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(responseId);
            ArgumentNullException.ThrowIfNull(conversationId);

            lock (_lock)
            {
                _continuations.Save(responseId, conversationId, _time.GetUtcNow());
            }

            return default;
        }

        /// <inheritdoc />
        public ValueTask<string?> FindContinuationAsync(string responseId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(responseId);

            lock (_lock)
            {
                return ValueTask.FromResult(_continuations.Find(responseId));
            }
        }

        /// <inheritdoc />
        public ValueTask DeleteContinuationAsync(string responseId, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(responseId);

            lock (_lock)
            {
                _continuations.Forget(responseId);
            }

            return default;
        }

        /// <summary>Drops a conversation, its claims, its state, its continuation and its words. Runs under the lock.</summary>
        private void Forget(string conversationId)
        {
            _ = _conversations.Remove(conversationId);
            _claims.Forget(conversationId);
            _ = _state.Remove(conversationId);
            _continuations.ForgetConversation(conversationId);
            _words.Forget(conversationId);
        }

        private ValueTask Amend(string conversationId, Func<ConversationRecord, ConversationRecord> amend)
        {
            lock (_lock)
            {
                if (_conversations.TryGetValue(conversationId, out ConversationRecord? conversation))
                {
                    _conversations[conversationId] = amend(conversation);
                }
            }

            return default;
        }
    }
}

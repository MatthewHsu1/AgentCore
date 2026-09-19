using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Conversation.Memory;

/// <summary>The store backing that keeps every row in this process: a conversation, and its words.</summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly Lock _lock = new();

    private readonly Dictionary<string, ConversationRecord> _conversations = [];

    private readonly Dictionary<(string ConversationId, int Ordinal), ConversationMessage> _rows = [];

    /// <summary>The resume blob of each conversation, beside the row rather than on it.</summary>
    private readonly Dictionary<string, ConversationSessionState> _state = [];

    /// <summary>The next free ordinal of each conversation. Never rewound, even when its words are.</summary>
    private readonly Dictionary<string, int> _nextOrdinal = [];

    private readonly HashSet<(string ConversationId, string PrincipalKey)> _claims = [];

    /// <summary>One serialized agent session per continuation id, beside the conversations.</summary>
    private readonly Dictionary<string, JsonElement> _continuations = [];

    private readonly TimeProvider _time;

    /// <summary>Creates the store.</summary>
    /// <param name="timeProvider">Where <c>created_at</c> comes from, or <see langword="null"/> for the system clock.</param>
    public InMemoryConversationStore(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public ValueTask<ConversationRecord> CreateAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        lock (_lock)
        {
            if (!_conversations.TryGetValue(conversationId, out var existing))
            {
                var now = _time.GetUtcNow();
                existing = new ConversationRecord(conversationId, null, ConversationStatus.Regular, null, null, now, null);
                _conversations[conversationId] = existing;
            }

            return ValueTask.FromResult(existing with
            {
                State = _state.GetValueOrDefault(conversationId),
                NextOrdinal = _nextOrdinal.GetValueOrDefault(conversationId),
            });
        }
    }

    /// <inheritdoc />
    public ValueTask<ConversationRecord?> GetAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        lock (_lock)
        {
            var conversation = _conversations.GetValueOrDefault(conversationId);

            return ValueTask.FromResult(conversation is null
                ? null
                : conversation with
                {
                    State = _state.GetValueOrDefault(conversationId),
                    NextOrdinal = _nextOrdinal.GetValueOrDefault(conversationId),
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

        var hasCursor = ConversationCursor.TryDecode(after, out var sortAt, out var cursorId);

        lock (_lock)
        {
            var ordered = _claims
                .Where(claim => claim.PrincipalKey == principalKey)
                .Select(claim => _conversations.GetValueOrDefault(claim.ConversationId))
                .OfType<ConversationRecord>()
                .Where(conversation => status is null || conversation.Status == status)
                .Where(conversation => !hasCursor
                    || SortValue(conversation) < sortAt
                    || (SortValue(conversation) == sortAt
                        && string.CompareOrdinal(conversation.ConversationId, cursorId) < 0))
                .OrderByDescending(SortValue)
                .ThenByDescending(conversation => conversation.ConversationId, StringComparer.Ordinal)
                .Take(limit)
                .ToList();

            var next = ordered.Count == limit
                ? ConversationCursor.Encode(SortValue(ordered[^1]), ordered[^1].ConversationId)
                : null;

            return ValueTask.FromResult(new ConversationPage(ordered, next));
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

        _conversations.Remove(conversationId);

        _claims.RemoveWhere(claim => claim.ConversationId == conversationId);

        _state.Remove(conversationId);

        _nextOrdinal.Remove(conversationId);

        _continuations.Remove(conversationId);

        RemoveWords(conversationId);

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
            var cutoff = _time.GetUtcNow() - retention;

            // SortValue is the clock ListAsync ranks by, so a conversation is swept exactly when it has
            // fallen off the end of the list.
            //
            // batchSize is read for its guard and then ignored. It exists to keep one transaction
            // short in a durable backing, and this store has no transaction; honouring it here would
            // only make the caller loop for the same answer.
            var going = _conversations.Values
                .Where(conversation => SortValue(conversation) < cutoff)
                .Select(conversation => conversation.ConversationId)
                .ToList();

            foreach (var conversationId in going)
            {
                _conversations.Remove(conversationId);

                _claims.RemoveWhere(claim => claim.ConversationId == conversationId);

                _state.Remove(conversationId);

                _nextOrdinal.Remove(conversationId);

                _continuations.Remove(conversationId);

                RemoveWords(conversationId);
            }

            return ValueTask.FromResult(going.Count);
        }
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
            _claims.Add((conversationId, principalKey));
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
            _claims.Remove((conversationId, principalKey));
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
            if (!_conversations.TryGetValue(conversationId, out var conversation))
            {
                throw new InvalidOperationException($"Store 0 holds no conversation '{conversationId}' to append words to.");
            }

            var fallbackTurnIndex = _state.GetValueOrDefault(conversationId)?.NextTurnIndex ?? 0;
            var first = _nextOrdinal.GetValueOrDefault(conversationId);

            var rows = new List<ConversationMessage>(messages.Count);
            for (var index = 0; index < messages.Count; index++)
            {
                var draft = messages[index];
                ConversationMessage row = new(
                    conversationId, first + index, draft.TurnIndex ?? fallbackTurnIndex, draft.Content, draft.MessageId);

                rows.Add(row);
                _rows.Add((row.ConversationId, row.Ordinal), row);
            }

            _nextOrdinal[conversationId] = first + messages.Count;

            var now = _time.GetUtcNow();
            _conversations[conversationId] = conversation with { LastMessageAt = now };

            if (state is not null)
            {
                _state[conversationId] = state;
            }

            return ValueTask.FromResult<IReadOnlyList<ConversationMessage>>(rows);
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
            foreach (var pair in _rows)
            {
                if (pair.Key.ConversationId != conversationId || pair.Value.MessageId != messageId)
                {
                    continue;
                }

                _rows[pair.Key] = pair.Value with { Content = content };
                break;
            }
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ConversationMessage>> ReadAsync(
        string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        lock (_lock)
        {
            IReadOnlyList<ConversationMessage> rows =
                [.. _rows.Values.Where(row => row.ConversationId == conversationId).OrderBy(row => row.Ordinal)];

            return ValueTask.FromResult(rows);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> TruncateAsync(
        string conversationId, int fromOrdinal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        lock (_lock)
        {
            var going = _rows.Keys
                .Where(key => string.Equals(key.ConversationId, conversationId, StringComparison.Ordinal)
                           && key.Ordinal >= fromOrdinal)
                .ToList();

            foreach (var key in going)
            {
                _rows.Remove(key);
            }

            return ValueTask.FromResult(going.Count);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> EraseAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        lock (_lock)
        {
            return ValueTask.FromResult(RemoveWords(conversationId));
        }
    }

    /// <inheritdoc />
    public ValueTask SaveContinuationAsync(string continuationId, JsonElement envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuationId);

        lock (_lock)
        {
            _continuations[continuationId] = envelope.Clone();
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask<JsonElement?> GetContinuationAsync(string continuationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuationId);

        lock (_lock)
        {
            return ValueTask.FromResult<JsonElement?>(
                _continuations.TryGetValue(continuationId, out var envelope) ? envelope : null);
        }
    }

    /// <inheritdoc />
    public ValueTask DeleteContinuationAsync(string continuationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuationId);

        lock (_lock)
        {
            _continuations.Remove(continuationId);
        }

        return default;
    }

    private static DateTimeOffset SortValue(ConversationRecord conversation) => conversation.LastMessageAt ?? conversation.CreatedAt;

    private int RemoveWords(string conversationId)
    {
        var going = _rows.Keys.Where(key => key.ConversationId == conversationId).ToList();
        foreach (var key in going)
        {
            _rows.Remove(key);
        }

        return going.Count;
    }

    private ValueTask Amend(string conversationId, Func<ConversationRecord, ConversationRecord> amend)
    {
        lock (_lock)
        {
            if (_conversations.TryGetValue(conversationId, out var conversation))
            {
                _conversations[conversationId] = amend(conversation);
            }
        }

        return default;
    }
}

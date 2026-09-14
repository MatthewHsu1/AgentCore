using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Calls.Memory;

/// <summary>The store backing that keeps every row in this process: a call, and its words.</summary>
public sealed class InMemoryCallStore : ICallStore
{
    private readonly Lock _lock = new();

    private readonly Dictionary<string, CallRecord> _calls = [];

    private readonly Dictionary<(string CallId, int Ordinal), CallMessage> _rows = [];

    /// <summary>The resume blob of each call, beside the row rather than on it.</summary>
    private readonly Dictionary<string, CallSessionState> _state = [];

    /// <summary>The next free ordinal of each call. Never rewound, even when its words are.</summary>
    private readonly Dictionary<string, int> _nextOrdinal = [];

    private readonly HashSet<(string CallId, string PrincipalKey)> _claims = [];

    /// <summary>One serialized agent session per continuation id, beside the calls.</summary>
    private readonly Dictionary<string, JsonElement> _continuations = [];

    private readonly TimeProvider _time;

    /// <summary>Creates the store.</summary>
    /// <param name="timeProvider">Where <c>created_at</c> comes from, or <see langword="null"/> for the system clock.</param>
    public InMemoryCallStore(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public ValueTask<CallRecord> CreateAsync(string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        lock (_lock)
        {
            if (!_calls.TryGetValue(callId, out var existing))
            {
                var now = _time.GetUtcNow();
                existing = new CallRecord(callId, null, CallStatus.Regular, null, null, now, null);
                _calls[callId] = existing;
            }

            return ValueTask.FromResult(existing with
            {
                State = _state.GetValueOrDefault(callId),
                NextOrdinal = _nextOrdinal.GetValueOrDefault(callId),
            });
        }
    }

    /// <inheritdoc />
    public ValueTask<CallRecord?> GetAsync(string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        lock (_lock)
        {
            var call = _calls.GetValueOrDefault(callId);
            
            return ValueTask.FromResult(call is null
                ? null
                : call with
                {
                    State = _state.GetValueOrDefault(callId),
                    NextOrdinal = _nextOrdinal.GetValueOrDefault(callId),
                });
        }
    }

    /// <inheritdoc />
    public ValueTask<CallPage> ListAsync(
        string principalKey,
        string? after,
        int limit,
        CallStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principalKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var hasCursor = CallCursor.TryDecode(after, out var sortAt, out var cursorId);

        lock (_lock)
        {
            var ordered = _claims
                .Where(claim => claim.PrincipalKey == principalKey)
                .Select(claim => _calls.GetValueOrDefault(claim.CallId))
                .OfType<CallRecord>()
                .Where(call => status is null || call.Status == status)
                .Where(call => !hasCursor
                    || SortValue(call) < sortAt
                    || (SortValue(call) == sortAt
                        && string.CompareOrdinal(call.CallId, cursorId) < 0))
                .OrderByDescending(SortValue)
                .ThenByDescending(call => call.CallId, StringComparer.Ordinal)
                .Take(limit)
                .ToList();

            var next = ordered.Count == limit
                ? CallCursor.Encode(SortValue(ordered[^1]), ordered[^1].CallId)
                : null;

            return ValueTask.FromResult(new CallPage(ordered, next));
        }
    }

    /// <inheritdoc />
    public ValueTask RenameAsync(string callId, string title, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(title);

        return Amend(callId, call => call with { Title = title });
    }

    /// <inheritdoc />
    public ValueTask SetStatusAsync(string callId, CallStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        return Amend(callId, call => call with { Status = status });
    }

    /// <inheritdoc />
    public ValueTask SetCustomAsync(
        string callId, JsonElement? custom, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        return Amend(callId, call => call with { Custom = custom?.Clone() });
    }

    /// <inheritdoc />
    public ValueTask SetExternalIdAsync(
        string callId, string? externalId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        return Amend(callId, call => call with { ExternalId = externalId });
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        lock (_lock)
        {
            _calls.Remove(callId);
            _claims.RemoveWhere(claim => claim.CallId == callId);
            _state.Remove(callId);
            _nextOrdinal.Remove(callId);
            RemoveWords(callId);
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
            var cutoff = _time.GetUtcNow() - retention;

            // SortValue is the clock ListAsync ranks by, so a call is swept exactly when it has
            // fallen off the end of the list.
            //
            // batchSize is read for its guard and then ignored. It exists to keep one transaction
            // short in a durable backing, and this store has no transaction; honouring it here would
            // only make the caller loop for the same answer.
            var going = _calls.Values
                .Where(call => SortValue(call) < cutoff)
                .Select(call => call.CallId)
                .ToList();

            foreach (var callId in going)
            {
                _calls.Remove(callId);
                _claims.RemoveWhere(claim => claim.CallId == callId);
                _state.Remove(callId);
                _nextOrdinal.Remove(callId);
                RemoveWords(callId);
            }

            return ValueTask.FromResult(going.Count);
        }
    }

    /// <inheritdoc />
    public ValueTask AttachPrincipalAsync(
        string callId, string principalKey, string role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(principalKey);
        ArgumentNullException.ThrowIfNull(role);

        lock (_lock)
        {
            _claims.Add((callId, principalKey));
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask DetachPrincipalAsync(
        string callId, string principalKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(principalKey);

        lock (_lock)
        {
            _claims.Remove((callId, principalKey));
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CallMessage>> AppendAsync(
        string callId,
        IReadOnlyList<CallMessageDraft> messages,
        CallSessionState? state = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<CallMessage>>([]);
        }

        lock (_lock)
        {
            if (!_calls.TryGetValue(callId, out var call))
            {
                throw new InvalidOperationException($"Store 0 holds no call '{callId}' to append words to.");
            }

            var fallbackTurnIndex = _state.GetValueOrDefault(callId)?.NextTurnIndex ?? 0;
            var first = _nextOrdinal.GetValueOrDefault(callId);

            var rows = new List<CallMessage>(messages.Count);
            for (var index = 0; index < messages.Count; index++)
            {
                var draft = messages[index];
                CallMessage row = new(
                    callId, first + index, draft.TurnIndex ?? fallbackTurnIndex, draft.Content, draft.MessageId);

                rows.Add(row);
                _rows.Add((row.CallId, row.Ordinal), row);
            }

            _nextOrdinal[callId] = first + messages.Count;

            var now = _time.GetUtcNow();
            _calls[callId] = call with { LastMessageAt = now };

            if (state is not null)
            {
                _state[callId] = state;
            }

            return ValueTask.FromResult<IReadOnlyList<CallMessage>>(rows);
        }
    }

    /// <inheritdoc />
    public ValueTask RewriteAsync(
        string callId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        ArgumentNullException.ThrowIfNull(content);

        lock (_lock)
        {
            foreach (var pair in _rows)
            {
                if (pair.Key.CallId != callId || pair.Value.MessageId != messageId)
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
    public ValueTask<IReadOnlyList<CallMessage>> ReadAsync(
        string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        lock (_lock)
        {
            IReadOnlyList<CallMessage> rows =
                [.. _rows.Values.Where(row => row.CallId == callId).OrderBy(row => row.Ordinal)];

            return ValueTask.FromResult(rows);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> TruncateAsync(
        string callId, int fromOrdinal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        lock (_lock)
        {
            var going = _rows.Keys
                .Where(key => string.Equals(key.CallId, callId, StringComparison.Ordinal)
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
    public ValueTask<int> EraseAsync(string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        lock (_lock)
        {
            return ValueTask.FromResult(RemoveWords(callId));
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

    private static DateTimeOffset SortValue(CallRecord call) => call.LastMessageAt ?? call.CreatedAt;

    private int RemoveWords(string callId)
    {
        var going = _rows.Keys.Where(key => key.CallId == callId).ToList();
        foreach (var key in going)
        {
            _rows.Remove(key);
        }

        return going.Count;
    }

    private ValueTask Amend(string callId, Func<CallRecord, CallRecord> amend)
    {
        lock (_lock)
        {
            if (_calls.TryGetValue(callId, out var call))
            {
                _calls[callId] = amend(call);
            }
        }

        return default;
    }
}

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

    private readonly HashSet<(string CallId, string PrincipalKey)> _claims = [];

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

            return ValueTask.FromResult(existing with { State = _state.GetValueOrDefault(callId) });
        }
    }

    /// <inheritdoc />
    public ValueTask<CallRecord?> GetAsync(string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        lock (_lock)
        {
            var call = _calls.GetValueOrDefault(callId);
            return ValueTask.FromResult(call is null ? null : call with { State = _state.GetValueOrDefault(callId) });
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
    public ValueTask AppendAsync(
        IReadOnlyList<CallMessage> messages,
        CallSessionState? state = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        lock (_lock)
        {
            var now = _time.GetUtcNow();

            foreach (var message in messages)
            {
                _rows.Add((message.CallId, message.Ordinal), message);

                if (_calls.TryGetValue(message.CallId, out var call))
                {
                    _calls[message.CallId] = call with { LastMessageAt = now };
                }
            }

            if (state is not null && messages.Count > 0)
            {
                _state[messages[0].CallId] = state;
            }
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask RewriteAsync(
        string callId, int ordinal, ChatMessage content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(content);

        lock (_lock)
        {
            if (_rows.TryGetValue((callId, ordinal), out var row))
            {
                _rows[(callId, ordinal)] = row with { Content = content };
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
    public ValueTask<int> EraseAsync(string callId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);

        lock (_lock)
        {
            return ValueTask.FromResult(RemoveWords(callId));
        }
    }

    /// <remarks>
    /// The same coalesce the PostgreSQL backing pages by. A NULL sort key would drop a call out of
    /// every page after the first, and here every call has one.
    /// </remarks>
    private static DateTimeOffset SortValue(CallRecord call) => call.LastMessageAt ?? call.CreatedAt;

    /// <remarks>The caller holds <see cref="_lock"/>.</remarks>
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

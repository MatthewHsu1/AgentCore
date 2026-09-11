using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using AgentCore.TestSupport;
using AgentCore.Application.Runtime;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes;

/// <summary>A store kept in this process, with the calls made against its words recorded.</summary>
internal sealed class RecordingCallStore() : DelegatingCallStore(new InMemoryCallStore())
{
    private readonly Lock _lock = new();

    /// <summary>The words as the store holds them now. It backs <see cref="Live"/>, not <see cref="Rows"/>.</summary>
    private readonly Dictionary<(string CallId, int Ordinal), CallMessage> _rows = [];

    /// <summary>Gets every row the provider appended, in the order it appended them.</summary>
    public List<CallMessage> Rows { get; } = [];

    /// <summary>Gets every rewrite the provider asked for, in order.</summary>
    public List<CallMessage> Rewrites { get; } = [];

    /// <summary>Gets how many times a whole call was read back.</summary>
    public int Reads { get; private set; }

    /// <summary>Reads one call as the store holds it now, oldest message first.</summary>
    /// <param name="callId">The call to read.</param>
    public IReadOnlyList<CallMessage> Live(string callId)
    {
        lock (_lock)
        {
            return [.. _rows.Values.Where(row => row.CallId == callId).OrderBy(row => row.Ordinal)];
        }
    }

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<CallMessage>> AppendAsync(
        string callId,
        IReadOnlyList<CallMessageDraft> messages,
        CallSessionState? state = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await base.AppendAsync(callId, messages, state, cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            Rows.AddRange(rows);
            foreach (var row in rows)
            {
                _rows[(row.CallId, row.Ordinal)] = row;
            }
        }

        return rows;
    }

    /// <inheritdoc />
    public override async ValueTask RewriteAsync(
        string callId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
    {
        await base.RewriteAsync(callId, messageId, content, cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            foreach (var pair in _rows)
            {
                if (pair.Key.CallId != callId || pair.Value.MessageId != messageId)
                {
                    continue;
                }

                var rewritten = pair.Value with { Content = content };
                _rows[pair.Key] = rewritten;
                Rewrites.Add(rewritten);
                break;
            }
        }
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<CallMessage>> ReadAsync(
        string callId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Reads++;
        }

        return ValueTask.FromResult(Live(callId));
    }

    /// <inheritdoc />
    public override ValueTask<int> EraseAsync(string callId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var going = _rows.Keys.Where(key => key.CallId == callId).ToList();
            foreach (var key in going)
            {
                _rows.Remove(key);
            }

            return ValueTask.FromResult(going.Count);
        }
    }
}

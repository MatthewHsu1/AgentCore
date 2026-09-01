using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using AgentCore.TestSupport;
using AgentCore.Application.Runtime;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes;

/// <summary>A store kept in this process, with the calls made against its words recorded.</summary>
/// <remarks>
/// It answers two different questions, because store 1 is asked two different things. <see cref="Rows"/>
/// and <see cref="Rewrites"/> are logs — what the provider ASKED the store to do, and in what order.
/// <see cref="Live"/> is the state those calls left behind, which is what a reader of the record
/// years later would see.
/// </remarks>
internal sealed class RecordingCallStore() : DelegatingCallStore(new InMemoryCallStore())
{
    private readonly Lock _lock = new();

    /// <summary>The words as the store holds them now. It backs <see cref="Live"/>, not <see cref="Rows"/>.</summary>
    /// <remarks>
    /// <see cref="Rows"/> is the log of what was ASKED for and grows forever; this is the record a
    /// reader would see, so a rewrite edits it in place and an erase empties it. It holds no
    /// <see cref="CallSessionState"/>: see the remarks on <see cref="AppendAsync"/>.
    /// </remarks>
    private readonly Dictionary<(string CallId, int Ordinal), CallMessage> _rows = [];

    /// <summary>Gets every row the provider appended, in the order it appended them.</summary>
    public List<CallMessage> Rows { get; } = [];

    /// <summary>Gets every rewrite the provider asked for, in order. <c>TurnIndex</c> is not carried.</summary>
    public List<CallMessage> Rewrites { get; } = [];

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
    /// <remarks>
    /// <paramref name="state"/> is DROPPED on purpose, and the drop is total: this override does not
    /// call the inner store, so nothing behind it sees the blob either. These tests ask store 1 what
    /// words it was handed and in what order, which is what the two logs answer; a resume that has to
    /// find its stage again wants a real store and not this one. Say so here rather than let a test
    /// that expects <c>GetAsync</c> to return the state read a null and go looking in the wrong file.
    /// </remarks>
    public override ValueTask AppendAsync(
        IReadOnlyList<CallMessage> messages,
        CallSessionState? state = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        lock (_lock)
        {
            Rows.AddRange(messages);
            foreach (var message in messages)
            {
                _rows.Add((message.CallId, message.Ordinal), message);
            }
        }

        return default;
    }

    /// <inheritdoc />
    public override ValueTask RewriteAsync(
        string callId, int ordinal, ChatMessage content, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Rewrites.Add(new CallMessage(callId, ordinal, TurnIndex: -1, content));

            if (_rows.TryGetValue((callId, ordinal), out var row))
            {
                _rows[(callId, ordinal)] = row with { Content = content };
            }
        }

        return default;
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<CallMessage>> ReadAsync(
        string callId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Live(callId));

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

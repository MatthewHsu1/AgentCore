using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes;

/// <summary>Store 1's backing, kept in this process, with the calls it was made recorded.</summary>
/// <remarks>
/// It answers two different questions, because store 1 is asked two different things. <see cref="Rows"/>
/// and <see cref="Rewrites"/> are logs — what the provider ASKED the store to do, and in what order.
/// <see cref="Live"/> is the state those calls left behind, which is what a reader of the record
/// years later would see.
/// </remarks>
internal sealed class RecordingCallMessageStore : ICallMessageStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string CallId, int Ordinal), CallMessage> _state = [];

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
            return [.. _state.Values.Where(row => row.CallId == callId).OrderBy(row => row.Ordinal)];
        }
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(
        IReadOnlyList<CallMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        lock (_lock)
        {
            Rows.AddRange(messages);
            foreach (var message in messages)
            {
                _state.Add((message.CallId, message.Ordinal), message);
            }
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask RewriteAsync(
        string callId, int ordinal, ChatMessage content, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Rewrites.Add(new CallMessage(callId, ordinal, TurnIndex: -1, content));

            if (_state.TryGetValue((callId, ordinal), out var row))
            {
                _state[(callId, ordinal)] = row with { Content = content };
            }
        }

        return default;
    }
}

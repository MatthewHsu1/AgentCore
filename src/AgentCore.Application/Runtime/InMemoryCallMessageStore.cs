using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>The store 1 backing that keeps every row in this process.</summary>
/// <remarks>
/// It is the default backing, and it is what a host that binds no database gets. The rows outlive
/// the call and nothing sweeps them, so it belongs to a test or a single-process demo and never to
/// a deployment that answers real calls.
/// </remarks>
internal sealed class InMemoryCallMessageStore : ICallMessageStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string CallId, int Ordinal), CallMessage> _rows = [];

    /// <inheritdoc />
    public ValueTask AppendAsync(IReadOnlyList<CallMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        lock (_lock)
        {
            foreach (var message in messages)
            {
                _rows.Add((message.CallId, message.Ordinal), message);
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
            if (_rows.TryGetValue((callId, ordinal), out var row))
            {
                _rows[(callId, ordinal)] = row with { Content = content };
            }
        }

        return default;
    }

    /// <summary>Reads one whole call, oldest message first.</summary>
    /// <param name="callId">The call to read.</param>
    public IReadOnlyList<CallMessage> Read(string callId)
    {
        lock (_lock)
        {
            return [.. _rows.Values.Where(row => row.CallId == callId).OrderBy(row => row.Ordinal)];
        }
    }
}

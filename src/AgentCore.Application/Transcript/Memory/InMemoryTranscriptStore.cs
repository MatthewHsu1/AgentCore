using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript.Memory;

/// <summary>The store backing that keeps every row in this process.</summary>
public sealed class InMemoryTranscriptStore : ITranscriptStore
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
    /// <returns>Every row of the call, or an empty list when it holds none.</returns>
    public IReadOnlyList<CallMessage> Read(string callId)
    {
        lock (_lock)
        {
            return [.. _rows.Values.Where(row => row.CallId == callId).OrderBy(row => row.Ordinal)];
        }
    }
}

using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes;

/// <summary>Refuses every write, the way a store 1 backing that is down does.</summary>
internal sealed class ThrowingTranscriptStore : ITranscriptStore
{
    public ValueTask AppendAsync(
        IReadOnlyList<CallMessage> messages, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("the transcript store is down.");

    public ValueTask RewriteAsync(
        string callId, int ordinal, ChatMessage content, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("the transcript store is down.");
}

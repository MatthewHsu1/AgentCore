using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes;

/// <summary>Refuses every write of words, the way a backing that is down does.</summary>
internal sealed class ThrowingCallStore() : DelegatingCallStore(new InMemoryCallStore())
{
    public override ValueTask AppendAsync(
        IReadOnlyList<CallMessage> messages,
        CallSessionState? state = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("the call store is down.");

    public override ValueTask RewriteAsync(
        string callId, int ordinal, ChatMessage content, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("the call store is down.");

    public override ValueTask<IReadOnlyList<CallMessage>> ReadAsync(
        string callId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("the call store is down.");

    public override ValueTask<int> EraseAsync(string callId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("the call store is down.");
}

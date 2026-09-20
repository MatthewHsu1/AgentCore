using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;

namespace AgentCore.TestSupport;

/// <summary>The oracle read a test wants: every row somebody said, oldest first.</summary>
public static class ConversationStoreReadAll
{
    public static ValueTask<IReadOnlyList<ConversationMessage>> ReadAllAsync(
        this IConversationStore store, string conversationId, CancellationToken cancellationToken = default)
        => store.ReadWindowAsync(conversationId, new TranscriptWindow(null, int.MaxValue), cancellationToken);
}

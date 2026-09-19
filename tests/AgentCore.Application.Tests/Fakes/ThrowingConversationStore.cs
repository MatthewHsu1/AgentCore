using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes;

/// <summary>Refuses every write of words, the way a backing that is down does.</summary>
internal sealed class ThrowingConversationStore() : DelegatingConversationStore(new InMemoryConversationStore())
{
    public override ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
        string conversationId,
        IReadOnlyList<ConversationMessageDraft> messages,
        ConversationSessionState? state = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("the conversation store is down.");

    public override ValueTask RewriteAsync(
        string conversationId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("the conversation store is down.");

    public override ValueTask<IReadOnlyList<ConversationMessage>> ReadAsync(
        string conversationId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("the conversation store is down.");

    public override ValueTask<int> EraseAsync(string conversationId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("the conversation store is down.");
}

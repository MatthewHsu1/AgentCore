using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// An in-memory store that holds the first append naming one turn until the test releases it, and reports the first
    /// time a turn found the conversation marked busy by another request. It is also its own adapter, of kind
    /// <c>test</c>.
    /// </summary>
    /// <param name="heldTurn">The turn whose first append is held.</param>
    internal sealed class TurnHoldingStore(int heldTurn) : DelegatingConversationStore(new InMemoryConversationStore()), IConversationStoreAdapter
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _markRefused = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _holding;

        public string Kind => "test";

        /// <summary>Gets a task that completes once the held append reached the store.</summary>
        public Task Held => _held.Task;

        /// <summary>Gets a task that completes the first time a mark was refused.</summary>
        public Task MarkRefused => _markRefused.Task;

        public void Release()
        {
            _ = _release.TrySetResult();
        }

        public ValueTask<IConversationStore> OpenAsync(
            VendorProviderConfiguration entry, ISecretResolverPort? secrets, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IConversationStore>(this);
        }

        public override async ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
            string conversationId,
            IReadOnlyList<ConversationMessageDraft> messages,
            ConversationSessionState? state = null,
            CancellationToken cancellationToken = default)
        {
            if (messages.Any(message => message.TurnIndex == heldTurn) && Interlocked.Exchange(ref _holding, 1) == 0)
            {
                _ = _held.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return await base.AppendAsync(conversationId, messages, state, cancellationToken);
        }

        public override async ValueTask<bool> TryMarkBusyAsync(
            string conversationId, string holder, TimeSpan lease, CancellationToken cancellationToken = default)
        {
            bool marked = await base.TryMarkBusyAsync(conversationId, holder, lease, cancellationToken);
            if (!marked)
            {
                _ = _markRefused.TrySetResult();
            }

            return marked;
        }
    }
}

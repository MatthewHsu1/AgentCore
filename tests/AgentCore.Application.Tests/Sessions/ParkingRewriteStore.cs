using AgentCore.Application.Conversation.Memory;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Sessions
{
    /// <summary>
    /// A store that holds every rewrite of a reply open until a test releases it. A rewrite is queued after the
    /// turn ends, so it is a write a session still owes with no turn running.
    /// </summary>
    internal sealed class ParkingRewriteStore() : DelegatingConversationStore(new InMemoryConversationStore())
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task that completes once a rewrite is parked.</summary>
        public Task Parked => _entered.Task;

        /// <summary>Lets every parked rewrite finish.</summary>
        public void Release()
        {
            _ = _release.TrySetResult();
        }

        /// <inheritdoc />
        public override async ValueTask RewriteAsync(
            string conversationId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
        {
            _ = _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            await base.RewriteAsync(conversationId, messageId, content, cancellationToken);
        }
    }
}

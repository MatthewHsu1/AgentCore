using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;

namespace AgentCore.Application.Tests.Fakes
{
    /// <summary>A store kept in this process whose next session read, once armed, holds until the test opens it.</summary>
    internal sealed class GatedReadConversationStore() : DelegatingConversationStore(new InMemoryConversationStore())
    {
        private volatile bool _armed;

        /// <summary>Gets what completes when the armed read reaches the gate.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets what opens the gate.</summary>
        public TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Makes the next session read wait at the gate.</summary>
        public void Arm()
        {
            _armed = true;
        }

        public override async ValueTask<IReadOnlyList<ConversationMessage>> ReadForSessionAsync(
            string conversationId, CancellationToken cancellationToken = default)
        {
            if (_armed)
            {
                _armed = false;
                _ = Entered.TrySetResult();
                await Open.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await base.ReadForSessionAsync(conversationId, cancellationToken).ConfigureAwait(false);
        }
    }
}

using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Transcript;

namespace AgentCore.TestSupport
{
    /// <summary>
    /// A store that holds its first write of words open until a test releases it.
    /// </summary>
    /// <remarks>
    /// A real store talks to a database, so a write outlives the turn that queued it. The memory store
    /// every other test runs on lands a write before the next line reads it, and would let teardown drop
    /// a session with a write still owing without any test noticing.
    /// </remarks>
    public sealed class ParkingConversationStore() : DelegatingConversationStore(new InMemoryConversationStore())
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _landed;

        /// <summary>Gets a task that completes once the first append is parked.</summary>
        public Task Parked => _entered.Task;

        /// <summary>Gets whether the parked append has finished writing.</summary>
        public bool Landed => _landed;

        /// <summary>Lets the parked append finish.</summary>
        public void Release()
        {
            _ = _release.TrySetResult();
        }

        /// <inheritdoc />
        public override async ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
            string conversationId,
            IReadOnlyList<ConversationMessageDraft> messages,
            ConversationSessionState? state = null,
            CancellationToken cancellationToken = default)
        {
            _ = _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            _landed = true;
            return await base.AppendAsync(conversationId, messages, state, cancellationToken);
        }
    }
}

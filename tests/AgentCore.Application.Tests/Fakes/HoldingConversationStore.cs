using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;

namespace AgentCore.Application.Tests.Fakes
{
    /// <summary>
    /// A store kept in this process that holds every record read, or every append, at a gate while the gate is
    /// armed, so a test pins one interleaving of a session's reads against its own writes.
    /// </summary>
    internal sealed class HoldingConversationStore() : DelegatingConversationStore(new InMemoryConversationStore())
    {
        private volatile TaskCompletionSource? _gets;

        private volatile TaskCompletionSource? _appends;

        /// <summary>Gets the signal that a record read reached an armed gate.</summary>
        public TaskCompletionSource GetHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the signal that an append reached an armed gate.</summary>
        public TaskCompletionSource AppendHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the signal that a session read of the words returned, since <see cref="HoldGets"/> armed the gate of record reads.</summary>
        public TaskCompletionSource SessionRead { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Arms the gate of record reads.</summary>
        public void HoldGets()
        {
            SessionRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _gets = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>Arms the gate of appends.</summary>
        public void HoldAppends()
        {
            _appends = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>Disarms the gate of record reads, and lets every held read go on.</summary>
        public void ReleaseGets()
        {
            TaskCompletionSource? gate = _gets;
            _gets = null;
            gate?.TrySetResult();
        }

        /// <summary>Disarms the gate of appends, and lets every held append go on.</summary>
        public void ReleaseAppends()
        {
            TaskCompletionSource? gate = _appends;
            _appends = null;
            gate?.TrySetResult();
        }

        /// <inheritdoc />
        public override async ValueTask<ConversationRecord?> GetAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            if (_gets is { } gate)
            {
                _ = GetHeld.TrySetResult();
                await gate.Task.ConfigureAwait(false);
            }

            return await base.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override async ValueTask<IReadOnlyList<ConversationMessage>> ReadForSessionAsync(
            string conversationId, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ConversationMessage> rows = await base.ReadForSessionAsync(conversationId, cancellationToken).ConfigureAwait(false);
            _ = SessionRead.TrySetResult();
            return rows;
        }

        /// <inheritdoc />
        public override async ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
            string conversationId,
            IReadOnlyList<ConversationMessageDraft> messages,
            ConversationSessionState? state = null,
            CancellationToken cancellationToken = default)
        {
            if (_appends is { } gate)
            {
                _ = AppendHeld.TrySetResult();
                await gate.Task.ConfigureAwait(false);
            }

            return await base.AppendAsync(conversationId, messages, state, cancellationToken).ConfigureAwait(false);
        }
    }
}

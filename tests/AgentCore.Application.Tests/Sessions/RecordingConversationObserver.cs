using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;

namespace AgentCore.Application.Tests.Sessions
{
    /// <summary>Records the kind of every event one conversation raised.</summary>
    internal sealed class RecordingConversationObserver : IConversationObserver
    {
        private readonly List<ConversationEventKind> _kinds = [];
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ConversationEventKind> Kinds
        {
            get
            {
                lock (_kinds)
                {
                    return [.. _kinds];
                }
            }
        }

        /// <summary>Gets a task that completes once <c>conversation.ended</c> arrives.</summary>
        public Task Ended => _ended.Task;

        public ValueTask OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken = default)
        {
            lock (_kinds)
            {
                _kinds.Add(conversationEvent.Kind);
            }

            if (conversationEvent.Kind == ConversationEventKind.ConversationEnded)
            {
                _ = _ended.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }
}

using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;

namespace AgentCore.Application.Tests.Fakes
{
    /// <summary>A store kept in this process whose appends fail while <see cref="Down"/> is set, like a backing that goes down for a while.</summary>
    internal sealed class FlakyAppendConversationStore() : DelegatingConversationStore(new InMemoryConversationStore())
    {
        /// <summary>Gets or sets whether every append fails.</summary>
        public bool Down { get; set; }

        /// <summary>
        /// Gets or sets whether every append is saved and then fails, like a commit that landed after which the
        /// connection dropped before the answer came back.
        /// </summary>
        public bool SavesThenFails { get; set; }

        /// <summary>Gets or sets how many of the next reads of the conversation's record fail.</summary>
        public int RecordReadsToFail { get; set; }

        public override ValueTask<ConversationRecord?> GetAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            if (RecordReadsToFail > 0)
            {
                RecordReadsToFail--;
                throw new InvalidOperationException("the conversation store cannot be read.");
            }

            return base.GetAsync(conversationId, cancellationToken);
        }

        public override async ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
            string conversationId,
            IReadOnlyList<ConversationMessageDraft> messages,
            ConversationSessionState? state = null,
            CancellationToken cancellationToken = default)
        {
            if (Down)
            {
                throw new InvalidOperationException("the conversation store is down.");
            }

            IReadOnlyList<ConversationMessage> rows = await base.AppendAsync(conversationId, messages, state, cancellationToken);
            return SavesThenFails ? throw new TimeoutException("the answer to a saved append was lost.") : rows;
        }
    }
}

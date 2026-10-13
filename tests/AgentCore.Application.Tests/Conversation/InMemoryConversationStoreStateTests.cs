using AgentCore.Application.Conversation.Memory;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.Application.Tests.Conversation
{
    /// <summary>The state facts of <see cref="ConversationStoreStateFacts"/>, against the in-memory store.</summary>
    public sealed class InMemoryConversationStoreStateTests
    {
        private static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public Task SaveState_LevelWithTheStoredState_ReplacesIt()
        {
            return ConversationStoreStateFacts.AStateLevelWithTheStoredOneReplacesIt(new InMemoryConversationStore(), Token);
        }

        [Fact]
        public Task SaveState_BehindTheStoredState_IsDropped()
        {
            return ConversationStoreStateFacts.AStateBehindTheStoredOneIsDropped(new InMemoryConversationStore(), Token);
        }

        [Fact]
        public Task SaveState_OfAConversationWithNoRow_LeavesItAlone()
        {
            return ConversationStoreStateFacts.AConversationWithNoRowIsLeftAlone(new InMemoryConversationStore(), Token);
        }
    }
}

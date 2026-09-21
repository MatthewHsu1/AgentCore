using AgentCore.Application.Conversation;
using Xunit;

namespace AgentCore.Application.Tests.Conversation
{
    /// <summary>The opaque cursor a listing hands back and takes again.</summary>
    public sealed class ConversationCursorTests
    {
        [Fact]
        public void Encode_ThenDecode_ReturnsBothValues()
        {
            // Arrange
            DateTimeOffset sortAt = new(2026, 8, 30, 12, 34, 56, 789, TimeSpan.Zero);

            // Act
            string cursor = ConversationCursor.Encode(sortAt, "conversation-1");
            bool decoded = ConversationCursor.TryDecode(cursor, out DateTimeOffset readAt, out string? readId);

            // Assert
            Assert.True(decoded);
            Assert.Equal(sortAt, readAt);
            Assert.Equal("conversation-1", readId);
        }

        [Fact]
        public void Encode_AConversationIdHoldingTheSeparator_RoundTripsWhole()
        {
            // Arrange
            const string ConversationId = "conversation|with|pipes";

            // Act
            string cursor = ConversationCursor.Encode(DateTimeOffset.UnixEpoch, ConversationId);
            _ = ConversationCursor.TryDecode(cursor, out _, out string? readId);

            // Assert
            Assert.Equal(ConversationId, readId);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-base64!")]
        [InlineData("bm90LWEtY3Vyc29y")]
        public void TryDecode_SomethingThatIsNotACursor_IsFalseAndNotAThrow(string? cursor)
        {
            // Act
            bool decoded = ConversationCursor.TryDecode(cursor, out _, out _);

            // Assert
            Assert.False(decoded);
        }
    }
}

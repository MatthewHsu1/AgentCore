using AgentCore.Application.Transcript;
using AgentCore.Domain.Sources;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>Pins the rules of store 1: ordinals, which reply a barge-in cuts, and what a cut keeps.</summary>
    public sealed class ConversationTranscriptTests
    {
        [Fact]
        public void Append_ToolResultCarriesASource_RowKeepsItAndMessagesStripsIt()
        {
            // Arrange
            ConversationTranscript transcript = new() { ConversationId = "conversation-1" };
            SourceContent cited = new()
            {
                Source = new SourceReference { SourceId = "order-41", Kind = SourceKind.Document, Title = "Order #41", Origin = "knowledge" },
                CallId = "conversation-1",
            };
            ChatMessage toolResult = new(
                ChatRole.Tool,
                [new FunctionResultContent("conversation-1", "50"), cited]);
            ChatMessage plain = Assistant("the price is fifty");

            // Act
            IReadOnlyList<ConversationMessage> rows = transcript.Append([toolResult, plain]);

            // Assert
            ChatMessage storedToolResult = transcript.Messages[0].Message;

            // The row is untouched: Append never rebuilds the message it hands to the store, so the
            // row is the exact original object, citation and tool result both.
            Assert.Same(toolResult, rows[0].Content);
            Assert.Contains(cited, rows[0].Content.Contents);
            _ = Assert.Single(rows[0].Content.Contents.OfType<FunctionResultContent>());

            Assert.DoesNotContain(storedToolResult.Contents, content => content is SourceContent);
            _ = Assert.Single(storedToolResult.Contents.OfType<FunctionResultContent>());
            Assert.Equal(rows[0].Content.Role, storedToolResult.Role);
            Assert.Equal(rows[0].Ordinal, transcript.Messages[0].Ordinal);
            Assert.Equal(rows[0].TurnIndex, transcript.Messages[0].TurnIndex);

            Assert.Same(plain, rows[1].Content);
            Assert.Same(plain, transcript.Messages[1].Message);
        }

        [Fact]
        public void Append_TwoTurns_AllocatesDenseUniqueOrdinals()
        {
            // Arrange
            ConversationTranscript transcript = new() { ConversationId = "conversation-1" };
            transcript.BeginTurn(0);
            _ = transcript.Append([User("hello"), Assistant("hi there")]);
            transcript.BeginTurn(1);

            // Act
            IReadOnlyList<ConversationMessage> rows = transcript.Append([User("order 41?"), Assistant("it ships Friday")]);

            // Assert
            Assert.Equal([2, 3], rows.Select(row => row.Ordinal));
            Assert.Equal([1, 1], rows.Select(row => row.TurnIndex));
        }

        [Fact]
        public void Append_ToolCallingTurn_AimsTheCutAtTheSpokenReply()
        {
            // Arrange
            ConversationTranscript transcript = new() { ConversationId = "conversation-1" };
            ChatMessage toolCall = new(ChatRole.Assistant, [new FunctionCallContent("id", "lookup")]);

            // Act
            _ = transcript.Append([User("order 41?"), toolCall, Assistant("it ships Friday")]);

            // Assert
            Assert.Equal(2, transcript.LastAssistantOrdinal);
        }

        [Fact]
        public void TruncateLastReply_ReplyCarryingUsage_KeepsEverythingButTheWords()
        {
            // Arrange
            ConversationTranscript transcript = new() { ConversationId = "conversation-1" };
            ChatMessage reply = new(
                ChatRole.Assistant,
                [new TextContent("it ships Friday from the depot"), new UsageContent(new UsageDetails { OutputTokenCount = 7 })]);
            _ = transcript.Append([User("order 41?"), reply]);

            // Act
            IReadOnlyList<ConversationMessage> rows = transcript.TruncateLastReply("it ships");

            // Assert
            ConversationMessage row = Assert.Single(rows);
            Assert.Equal("it ships", row.Content.Text);
            Assert.Equal(7, row.Content.Contents.OfType<UsageContent>().Single().Details.OutputTokenCount);
        }

        [Fact]
        public void TruncateLastReply_TheConversationSpokeNothing_RewritesNoRow()
        {
            // Arrange
            ConversationTranscript transcript = new() { ConversationId = "conversation-1" };
            _ = transcript.Append([User("hello")]);

            // Act
            IReadOnlyList<ConversationMessage> rows = transcript.TruncateLastReply("nothing was said");

            // Assert
            Assert.Empty(rows);
        }

        /// <summary>
        /// A model routinely writes a line and puts the tool call it announces on the same message. The
        /// caller heard as much of the turn as the vendor played and nothing else, so the announcement
        /// must not survive the cut as words the caller is recorded as having heard.
        /// </summary>
        [Fact]
        public void TruncateLastReply_ProseBesideAToolCall_KeepsTheConversationAndDropsTheWords()
        {
            // Arrange
            ConversationTranscript transcript = new() { ConversationId = "conversation-1" };
            ChatMessage announced = new(
                ChatRole.Assistant,
                [new TextContent("Let me check that for you"), new FunctionCallContent("conversation-1", "lookup")]);
            _ = transcript.Append(
                [
                    User("how much?"),
                    announced,
                    new ChatMessage(ChatRole.Tool, [new FunctionResultContent("conversation-1", "50")]),
                    Assistant("the price is fifty"),
                ]);

            // Act
            IReadOnlyList<ConversationMessage> rows = transcript.TruncateLastReply("the price");

            // Assert
            Assert.Equal([1, 3], rows.Select(row => row.Ordinal));
            Assert.Equal([string.Empty, "the price"], rows.Select(row => row.Content.Text));
            _ = Assert.Single(rows[0].Content.Contents.OfType<FunctionCallContent>());
        }

        /// <summary>
        /// The held prompt of item 6a opens turn 1 while the vendor is still speaking turn 0, so the
        /// reply a barge-in cuts is the one before the turn now open. Whether that turn may still be
        /// corrected is <c>ConversationSession</c>'s decision, and this class does not take it away.
        /// </summary>
        [Fact]
        public void BeginTurn_NextTurnOpens_LeavesThePreviousReplyOpenToACut()
        {
            // Arrange
            ConversationTranscript transcript = new() { ConversationId = "conversation-1" };
            transcript.BeginTurn(0);
            _ = transcript.Append([User("hello"), Assistant("hi there")]);

            // Act
            transcript.BeginTurn(1);

            // Assert
            Assert.Equal(1, transcript.LastAssistantOrdinal);
            ConversationMessage row = Assert.Single(transcript.TruncateLastReply("hi"));
            Assert.Equal(0, row.TurnIndex);
        }

        [Fact]
        public void Read_AfterTruncate_ReturnsHeardTextNotProducedText()
        {
            // Arrange
            ConversationTranscript transcript = new() { ConversationId = "conversation-1" };
            _ = transcript.Append([User("order 41?"), Assistant("it ships Friday from the depot")]);

            // Act
            _ = transcript.TruncateLastReply("it ships Fri");

            // Assert
            Assert.Equal(["order 41?", "it ships Fri"], transcript.Read().Select(message => message.Text));
        }

        private static ChatMessage User(string text)
        {
            return new(ChatRole.User, text);
        }

        private static ChatMessage Assistant(string text)
        {
            return new(ChatRole.Assistant, text);
        }
    }
}

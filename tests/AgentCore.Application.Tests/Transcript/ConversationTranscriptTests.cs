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
        public void RewriteReply_ReplyCarryingUsage_KeepsEverythingButTheWords()
        {
            // Arrange
            ConversationTranscript transcript = new() { ConversationId = "conversation-1" };
            ChatMessage reply = new(
                ChatRole.Assistant,
                [new TextContent("it ships Friday from the depot"), new UsageContent(new UsageDetails { OutputTokenCount = 7 })]);
            _ = transcript.Append([User("order 41?"), reply]);

            // Act
            IReadOnlyList<ConversationMessage> rows = transcript.RewriteReply("it ships").Rewritten;

            // Assert
            ConversationMessage row = Assert.Single(rows);
            Assert.Equal("it ships", row.Content.Text);
            Assert.Equal(7, row.Content.Contents.OfType<UsageContent>().Single().Details.OutputTokenCount);
        }

        [Fact]
        public void RewriteReply_TheConversationSpokeNothing_RewritesNoRow()
        {
            // Arrange
            ConversationTranscript transcript = new() { ConversationId = "conversation-1" };
            _ = transcript.Append([User("hello")]);

            // Act
            IReadOnlyList<ConversationMessage> rows = transcript.RewriteReply("nothing was said").Rewritten;

            // Assert
            Assert.Empty(rows);
        }

        /// <summary>
        /// A model routinely writes a line and puts the tool call it announces on the same message, then
        /// answers in a second step. The caller heard the first step whole and part of the second, so the
        /// first step's words stay before their tool call and only the second step is cut (owner ruling
        /// 2026-09-23; LiveKit keeps each step's forwarded_text as its own message).
        /// </summary>
        [Fact]
        public void RewriteReply_ProseBesideAToolCall_KeepsTheProseInPlaceAndCutsTheLastStep()
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
            IReadOnlyList<ConversationMessage> rows = transcript.RewriteReply("Let me check that for youthe price").Rewritten;

            // Assert
            ConversationMessage row = Assert.Single(rows);
            Assert.Equal(3, row.Ordinal);
            Assert.Equal("the price", row.Content.Text);
            Assert.Equal(
                ["how much?", "Let me check that for you", string.Empty, "the price"],
                transcript.Messages.Select(stored => stored.Message.Text));
            _ = Assert.Single(transcript.Messages[1].Message.Contents.OfType<FunctionCallContent>());
        }

        /// <summary>
        /// The caller barged in on the first step's own audio, after its tool ran but before the second step
        /// spoke. The first step is cut to what was heard, and the second step, left carrying nothing, is removed
        /// rather than kept as an empty assistant message (LiveKit adds a message only <c>if forwarded_text:</c>,
        /// agent_activity.py:3335).
        /// </summary>
        [Fact]
        public void RewriteReply_HeardEndsInsideTheFirstStep_CutsTheFirstStepAndRemovesTheSecond()
        {
            // Arrange
            ConversationTranscript transcript = new() { ConversationId = "conversation-1" };
            _ = transcript.Append(
                [
                    User("how much?"),
                    new ChatMessage(
                        ChatRole.Assistant,
                        [new TextContent("Let me check that for you"), new FunctionCallContent("conversation-1", "lookup")]),
                    new ChatMessage(ChatRole.Tool, [new FunctionResultContent("conversation-1", "50")]),
                    Assistant("the price is fifty"),
                ]);

            // Act
            ReplyRewrite rewrite = transcript.RewriteReply("Let me check");

            // Assert
            ConversationMessage row = Assert.Single(rewrite.Rewritten);
            Assert.Equal(1, row.Ordinal);
            Assert.Equal("Let me check", row.Content.Text);
            _ = Assert.Single(row.Content.Contents.OfType<FunctionCallContent>());
            Assert.Equal(3, Assert.Single(rewrite.Removed).Ordinal);
            Assert.Equal([0, 1, 2], transcript.Messages.Select(stored => stored.Ordinal));
        }

        /// <summary>
        /// A caller who heard nothing of a one-message reply leaves that message carrying nothing, so it goes, and
        /// the model is never read an assistant message with no content.
        /// </summary>
        [Fact]
        public void RewriteReply_NothingWasHeard_RemovesTheReplyAndReadsOnlyTheQuestion()
        {
            // Arrange
            ConversationTranscript transcript = new() { ConversationId = "conversation-1" };
            _ = transcript.Append([User("hi"), Assistant("Hello there.")]);

            // Act
            ReplyRewrite rewrite = transcript.RewriteReply(string.Empty);

            // Assert
            Assert.Empty(rewrite.Rewritten);
            Assert.Equal(1, Assert.Single(rewrite.Removed).Ordinal);
            Assert.Equal(["hi"], transcript.Read().Select(message => message.Text));
            Assert.Null(transcript.LastAssistantOrdinal);
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
            ConversationMessage row = Assert.Single(transcript.RewriteReply("hi").Rewritten);
            Assert.Equal(0, row.TurnIndex);
        }

        [Fact]
        public void Read_AfterTruncate_ReturnsHeardTextNotProducedText()
        {
            // Arrange
            ConversationTranscript transcript = new() { ConversationId = "conversation-1" };
            _ = transcript.Append([User("order 41?"), Assistant("it ships Friday from the depot")]);

            // Act
            _ = transcript.RewriteReply("it ships Fri");

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

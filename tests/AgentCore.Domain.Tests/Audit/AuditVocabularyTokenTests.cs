using AgentCore.Domain.Audit;
using Xunit;

namespace AgentCore.Domain.Tests.Audit
{
    /// <summary>
    /// Every closed vocabulary of the chain names each value by one stable token, and refuses any other.
    /// </summary>
    public sealed class AuditVocabularyTokenTests
    {
        [Theory]
        [InlineData(AuditEventKind.ConversationStarted, "conversation.started")]
        [InlineData(AuditEventKind.TurnCompleted, "turn.completed")]
        [InlineData(AuditEventKind.ReplyInterrupted, "reply.interrupted")]
        [InlineData(AuditEventKind.ToolFailed, "tool.failed")]
        [InlineData(AuditEventKind.PromptFlagged, "prompt.flagged")]
        [InlineData(AuditEventKind.ConversationEnded, "conversation.ended")]
        [InlineData(AuditEventKind.TurnRefused, "turn.refused")]
        public void EachKind_HasItsToken(AuditEventKind kind, string token)
        {
            // The token is stable forever. A C# rename must not change a hash PostgreSQL already stored.
            Assert.Equal(token, AuditEventKinds.ToToken(kind));
            Assert.True(AuditEventKinds.TryParse(token, out AuditEventKind parsed));
            Assert.Equal(kind, parsed);
        }

        [Fact]
        public void TheVocabulary_IsClosed()
        {
            AuditEventKind[] declared = Enum.GetValues<AuditEventKind>();

            // Eight kinds, and the numbers run 0 to 8 with 4 missing. Value 4 was reply.flagged, retired
            // on 2026-08-13 when reply moderation was withdrawn. A number is never reused, so a stored
            // row can never come to mean something else.
            Assert.Equal(8, declared.Length);
            Assert.DoesNotContain(declared, kind => (int)kind == 4);
            foreach (AuditEventKind kind in declared)
            {
                Assert.NotEmpty(AuditEventKinds.ToToken(kind));
            }

            _ = Assert.Throws<ArgumentOutOfRangeException>(() => AuditEventKinds.ToToken((AuditEventKind)99));
            Assert.False(AuditEventKinds.TryParse("conversation.transferred", out _));
        }

        [Theory]
        [InlineData(ConversationEndReason.CallerHungUp, "caller.hangup")]
        [InlineData(ConversationEndReason.AgentCompleted, "agent.completed")]
        [InlineData(ConversationEndReason.TransferredToHuman, "agent.transferred")]
        [InlineData(ConversationEndReason.Faulted, "conversation.faulted")]
        public void EachEndReason_HasItsToken(ConversationEndReason reason, string token)
        {
            // The token is stable forever, for the reason a kind token is. A report counts these years
            // after the conversation, and the count breaks when a token moves.
            Assert.Equal(token, ConversationEndReasons.ToToken(reason));
            Assert.True(ConversationEndReasons.TryParse(token, out ConversationEndReason parsed));
            Assert.Equal(reason, parsed);
        }

        [Fact]
        public void TheEndReasons_AreClosed()
        {
            ConversationEndReason[] declared = Enum.GetValues<ConversationEndReason>();

            // Section 4 names four ways a conversation ends, and the set holds those four and no more.
            Assert.Equal(4, declared.Length);
            foreach (ConversationEndReason reason in declared)
            {
                Assert.NotEmpty(ConversationEndReasons.ToToken(reason));
            }

            _ = Assert.Throws<ArgumentOutOfRangeException>(() => ConversationEndReasons.ToToken((ConversationEndReason)99));
            Assert.False(ConversationEndReasons.TryParse("hangup", out _));
            Assert.False(ConversationEndReasons.TryParse("caller hung up", out _));
            Assert.False(ConversationEndReasons.TryParse("CallerHungUp", out _));
            Assert.False(ConversationEndReasons.TryParse("0", out _));
        }

        [Theory]
        [InlineData(ToolFailureKind.Undeclared, "tool.undeclared")]
        [InlineData(ToolFailureKind.Faulted, "tool.faulted")]
        public void EachToolFailureKind_HasItsToken(ToolFailureKind kind, string token)
        {
            // The token is stable forever, for the reason an end reason's is. A report that counts how
            // often the model invented a tool name reads this token years after the conversation.
            Assert.Equal(token, ToolFailureKinds.ToToken(kind));
            Assert.True(ToolFailureKinds.TryParse(token, out ToolFailureKind parsed));
            Assert.Equal(kind, parsed);
        }

        [Fact]
        public void TheToolFailureKinds_AreClosed()
        {
            ToolFailureKind[] declared = Enum.GetValues<ToolFailureKind>();

            // Two facts, and no more: the model named a tool the document does not declare, or a
            // declared tool threw. The framework's own status set is wider and it is not ours.
            Assert.Equal(2, declared.Length);
            foreach (ToolFailureKind kind in declared)
            {
                Assert.NotEmpty(ToolFailureKinds.ToToken(kind));
            }

            _ = Assert.Throws<ArgumentOutOfRangeException>(() => ToolFailureKinds.ToToken((ToolFailureKind)99));
            Assert.False(ToolFailureKinds.TryParse("NotFound", out _));
            Assert.False(ToolFailureKinds.TryParse("Exception", out _));
            Assert.False(ToolFailureKinds.TryParse("1", out _));
        }
    }
}

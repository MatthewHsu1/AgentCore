using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// What one conversation event kind is to the audit vocabulary, and what it is called in a log line.
/// </summary>
public sealed class ConversationEventKindsTests
{
    /// <summary>Every kind the chain stores, beside the row it writes and the token it hashes.</summary>
    public static TheoryData<ConversationEventKind, AuditEventKind, string> StoredKinds =>
        new()
        {
            { ConversationEventKind.ConversationStarted, AuditEventKind.ConversationStarted, "conversation.started" },
            { ConversationEventKind.PromptFlagged, AuditEventKind.PromptFlagged, "prompt.flagged" },
            { ConversationEventKind.ToolFailed, AuditEventKind.ToolFailed, "tool.failed" },
            { ConversationEventKind.TurnCompleted, AuditEventKind.TurnCompleted, "turn.completed" },
            { ConversationEventKind.ReplyInterrupted, AuditEventKind.ReplyInterrupted, "reply.interrupted" },
            { ConversationEventKind.ConversationEnded, AuditEventKind.ConversationEnded, "conversation.ended" },
            { ConversationEventKind.TurnSuperseded, AuditEventKind.TurnSuperseded, "turn.superseded" },
        };

    /// <summary>Every diagnostic kind, beside the name a log line gives it.</summary>
    public static TheoryData<ConversationEventKind, string> DiagnosticKinds =>
        new()
        {
            { ConversationEventKind.ModerationUnavailable, "moderation.unavailable" },
            { ConversationEventKind.ModerationClean, "moderation.clean" },
            { ConversationEventKind.EmptyReply, "reply.empty" },
            { ConversationEventKind.ExtractionFailed, "extraction.failed" },
            { ConversationEventKind.TranscriptWriteFailed, "transcript.write.failed" },
            { ConversationEventKind.StateRestorePartial, "state.restore.partial" },
        { ConversationEventKind.TranscriptResyncFailed, "transcript.resync.failed" },
        };

    [Theory]
    [MemberData(nameof(StoredKinds))]
    public void AStoredKind_NamesItsRowAndItsToken(ConversationEventKind kind, AuditEventKind expected, string token)
    {
        Assert.True(ConversationEventKinds.TryGetAuditKind(kind, out AuditEventKind mapped));
        Assert.Equal(expected, mapped);

        // The token is the chain's, and it is a permanent promise. Nothing here invents one.
        Assert.Equal(token, ConversationEventKinds.ToToken(kind));
        Assert.Equal(AuditEventKinds.ToToken(expected), ConversationEventKinds.ToToken(kind));
    }

    [Theory]
    [MemberData(nameof(DiagnosticKinds))]
    public void ADiagnosticKind_MapsToNoRowAndIsStillNamed(ConversationEventKind kind, string token)
    {
        Assert.False(ConversationEventKinds.TryGetAuditKind(kind, out _));

        // It reaches no chain, so AuditEventKinds knows no token for it. A report still has to name it.
        Assert.Equal(token, ConversationEventKinds.ToToken(kind));
    }

    [Fact]
    public void EveryKind_IsEitherStoredOrDiagnostic_AndEveryOneIsCovered()
    {
        var stored = Enum.GetValues<ConversationEventKind>()
            .Count(kind => ConversationEventKinds.TryGetAuditKind(kind, out _));

        Assert.Equal(14, Enum.GetValues<ConversationEventKind>().Length);
        Assert.Equal(7, stored);
    }

    [Fact]
    public void AValueOutsideTheClosedSet_IsNamedRatherThanThrownOver()
    {
        // A kind the enum does not hold must not cost a report the fault it is carrying.
        const ConversationEventKind Unknown = (ConversationEventKind)999;

        Assert.False(ConversationEventKinds.TryGetAuditKind(Unknown, out _));
        Assert.Equal("999", ConversationEventKinds.ToToken(Unknown));
    }
}

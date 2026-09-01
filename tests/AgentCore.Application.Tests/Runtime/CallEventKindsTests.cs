using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// What one call event kind is to the audit vocabulary, and what it is called in a log line.
/// </summary>
public sealed class CallEventKindsTests
{
    /// <summary>Every kind the chain stores, beside the row it writes and the token it hashes.</summary>
    public static TheoryData<CallEventKind, AuditEventKind, string> StoredKinds =>
        new()
        {
            { CallEventKind.CallStarted, AuditEventKind.CallStarted, "call.started" },
            { CallEventKind.PromptFlagged, AuditEventKind.PromptFlagged, "prompt.flagged" },
            { CallEventKind.ToolFailed, AuditEventKind.ToolFailed, "tool.failed" },
            { CallEventKind.TurnCompleted, AuditEventKind.TurnCompleted, "turn.completed" },
            { CallEventKind.ReplyInterrupted, AuditEventKind.ReplyInterrupted, "reply.interrupted" },
            { CallEventKind.CallEnded, AuditEventKind.CallEnded, "call.ended" },
            { CallEventKind.TurnSuperseded, AuditEventKind.TurnSuperseded, "turn.superseded" },
        };

    /// <summary>Every diagnostic kind, beside the name a log line gives it.</summary>
    public static TheoryData<CallEventKind, string> DiagnosticKinds =>
        new()
        {
            { CallEventKind.ModerationUnavailable, "moderation.unavailable" },
            { CallEventKind.ModerationClean, "moderation.clean" },
            { CallEventKind.EmptyReply, "reply.empty" },
            { CallEventKind.ExtractionFailed, "extraction.failed" },
            { CallEventKind.TranscriptWriteFailed, "transcript.write.failed" },
            { CallEventKind.StateRestorePartial, "state.restore.partial" },
        };

    [Theory]
    [MemberData(nameof(StoredKinds))]
    public void AStoredKind_NamesItsRowAndItsToken(CallEventKind kind, AuditEventKind expected, string token)
    {
        Assert.True(CallEventKinds.TryGetAuditKind(kind, out AuditEventKind mapped));
        Assert.Equal(expected, mapped);

        // The token is the chain's, and it is a permanent promise. Nothing here invents one.
        Assert.Equal(token, CallEventKinds.ToToken(kind));
        Assert.Equal(AuditEventKinds.ToToken(expected), CallEventKinds.ToToken(kind));
    }

    [Theory]
    [MemberData(nameof(DiagnosticKinds))]
    public void ADiagnosticKind_MapsToNoRowAndIsStillNamed(CallEventKind kind, string token)
    {
        Assert.False(CallEventKinds.TryGetAuditKind(kind, out _));

        // It reaches no chain, so AuditEventKinds knows no token for it. A report still has to name it.
        Assert.Equal(token, CallEventKinds.ToToken(kind));
    }

    [Fact]
    public void EveryKind_IsEitherStoredOrDiagnostic_AndEveryOneIsCovered()
    {
        var stored = Enum.GetValues<CallEventKind>()
            .Count(kind => CallEventKinds.TryGetAuditKind(kind, out _));

        Assert.Equal(13, Enum.GetValues<CallEventKind>().Length);
        Assert.Equal(7, stored);
    }

    [Fact]
    public void AValueOutsideTheClosedSet_IsNamedRatherThanThrownOver()
    {
        // A kind the enum does not hold must not cost a report the fault it is carrying.
        const CallEventKind Unknown = (CallEventKind)999;

        Assert.False(CallEventKinds.TryGetAuditKind(Unknown, out _));
        Assert.Equal("999", CallEventKinds.ToToken(Unknown));
    }
}

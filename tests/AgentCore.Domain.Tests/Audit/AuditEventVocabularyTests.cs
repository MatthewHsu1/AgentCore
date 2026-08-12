using AgentCore.Domain.Audit;
using Xunit;

namespace AgentCore.Domain.Tests.Audit;

/// <summary>
/// Both vocabularies are closed, their tokens are stable, and an amendment names the event it
/// amends.
/// </summary>
public sealed class AuditEventVocabularyTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    [Theory]
    [InlineData(AuditEventKind.CallStarted, "call.started")]
    [InlineData(AuditEventKind.TurnCompleted, "turn.completed")]
    [InlineData(AuditEventKind.ReplyInterrupted, "reply.interrupted")]
    [InlineData(AuditEventKind.ToolFailed, "tool.failed")]
    [InlineData(AuditEventKind.ReplyFlagged, "reply.flagged")]
    [InlineData(AuditEventKind.CallEnded, "call.ended")]
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

        Assert.Equal(6, declared.Length);
        foreach (AuditEventKind kind in declared)
        {
            Assert.NotEmpty(AuditEventKinds.ToToken(kind));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => AuditEventKinds.ToToken((AuditEventKind)99));
        Assert.False(AuditEventKinds.TryParse("call.transferred", out _));
    }

    [Theory]
    [InlineData(CallEndReason.CallerHungUp, "caller.hangup")]
    [InlineData(CallEndReason.AgentCompleted, "agent.completed")]
    [InlineData(CallEndReason.TransferredToHuman, "agent.transferred")]
    [InlineData(CallEndReason.Faulted, "call.faulted")]
    public void EachEndReason_HasItsToken(CallEndReason reason, string token)
    {
        // The token is stable forever, for the reason a kind token is. A report counts these years
        // after the call, and the count breaks when a token moves.
        Assert.Equal(token, CallEndReasons.ToToken(reason));
        Assert.True(CallEndReasons.TryParse(token, out CallEndReason parsed));
        Assert.Equal(reason, parsed);
    }

    [Fact]
    public void TheEndReasons_AreClosed()
    {
        CallEndReason[] declared = Enum.GetValues<CallEndReason>();

        // Section 4 names four ways a call ends, and the set holds those four and no more.
        Assert.Equal(4, declared.Length);
        foreach (CallEndReason reason in declared)
        {
            Assert.NotEmpty(CallEndReasons.ToToken(reason));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => CallEndReasons.ToToken((CallEndReason)99));
        Assert.False(CallEndReasons.TryParse("hangup", out _));
        Assert.False(CallEndReasons.TryParse("caller hung up", out _));
        Assert.False(CallEndReasons.TryParse("CallerHungUp", out _));
        Assert.False(CallEndReasons.TryParse("0", out _));
    }

    /// <summary>The reason is counted, so the chain refuses free text under it.</summary>
    [Fact]
    public void ACallEndedEventWithAFreeTextReason_IsRefused()
    {
        AuditEvent free = Ended() with
        {
            Payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.EndReason] = "the caller hung up",
            },
        };

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => AuditChain.Link(free, AuditHash.Genesis));

        Assert.Contains(AuditPayloadKeys.EndReason, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACallEndedEventWithoutAReason_IsRefused()
    {
        AuditEvent silent = Ended() with
        {
            Payload = new Dictionary<string, string>(StringComparer.Ordinal),
        };

        Assert.Throws<ArgumentException>(() => AuditChain.Link(silent, AuditHash.Genesis));
    }

    /// <summary>
    /// T23: the chain is append-only, so a barge-in is a second event that references the first.
    /// </summary>
    [Fact]
    public void AnAmendment_ReferencesTheEventItAmends()
    {
        AuditEvent turn = Turn(sequence: 4, turnIndex: 2);
        AuditEvent interruption = Interruption(sequence: 5, amends: turn.Sequence, turnIndex: 2);

        IReadOnlyList<AuditChainLink> links = AuditChain.LinkAll([turn, interruption]);

        Assert.True(AuditChain.Verify(links).IsIntact);
        Assert.Equal(turn.Sequence, links[1].Event.AmendsSequence);
        Assert.Equal(turn.CallId, links[1].Event.CallId);
        Assert.Equal(turn.TurnIndex, links[1].Event.TurnIndex);

        // The first event is untouched. Nothing rewrote the turn, and both events stand in the chain.
        Assert.Equal("Welcome to Sole, how can I help you today?", links[0].Event.Payload[AuditPayloadKeys.ReplyText]);
        Assert.Null(links[0].Event.AmendsSequence);
    }

    /// <summary>Section 11, item 6a: the event records the text the caller ACTUALLY HEARD.</summary>
    [Fact]
    public void AnInterruption_RecordsWhatTheCallerHeardAndNotWhatTheModelProduced()
    {
        AuditEvent turn = Turn(sequence: 0, turnIndex: 0);
        AuditEvent interruption = Interruption(sequence: 1, amends: 0, turnIndex: 0);

        string produced = turn.Payload[AuditPayloadKeys.ReplyText];
        string heard = interruption.Payload[AuditPayloadKeys.UtteranceUntilInterrupt];

        Assert.NotEqual(produced, heard);
        Assert.StartsWith(heard, produced, StringComparison.Ordinal);
        Assert.Equal("1820", interruption.Payload[AuditPayloadKeys.DurationUntilInterruptMs]);
    }

    [Fact]
    public void AnInterruptionThatAmendsNothing_IsRefused()
    {
        AuditEvent orphan = Interruption(sequence: 1, amends: 0, turnIndex: 0) with { AmendsSequence = null };

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => AuditChain.Link(orphan, AuditHash.Genesis));

        Assert.Contains("T23", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInterruptionWithoutTheUtterance_IsRefused()
    {
        AuditEvent silent = Interruption(sequence: 1, amends: 0, turnIndex: 0) with
        {
            Payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.DurationUntilInterruptMs] = "1820",
            },
        };

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => AuditChain.Link(silent, AuditHash.Genesis));

        Assert.Contains(AuditPayloadKeys.UtteranceUntilInterrupt, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAmendmentThatNamesALaterEvent_IsRefused()
    {
        AuditEvent backwards = Interruption(sequence: 1, amends: 0, turnIndex: 0) with { AmendsSequence = 9 };

        Assert.Throws<ArgumentException>(() => AuditChain.Link(backwards, AuditHash.Genesis));
    }

    [Fact]
    public void AnEventWithoutACallId_IsRefused()
    {
        AuditEvent nameless = Turn(sequence: 0, turnIndex: 0) with { CallId = string.Empty };

        Assert.Throws<ArgumentException>(() => AuditChain.Link(nameless, AuditHash.Genesis));
    }

    [Fact]
    public void AHash_RefusesEverySpellingButLowercaseHexadecimal()
    {
        // PostgreSQL renders sha256() through encode(..., 'hex'), which is lowercase.
        Assert.Throws<ArgumentException>(() => AuditHash.Parse(new string('A', AuditHash.Length)));
        Assert.Throws<ArgumentException>(() => AuditHash.Parse(new string('0', AuditHash.Length - 1)));
        Assert.Throws<ArgumentException>(() => AuditHash.Parse("not a hash"));
        Assert.False(AuditHash.TryParse(null, out _));
        Assert.Equal(new string('0', AuditHash.Length), AuditHash.Genesis.Value);
    }

    private static AuditEvent Turn(long sequence, int turnIndex) => new()
    {
        CallId = "call-1",
        Sequence = sequence,
        Kind = AuditEventKind.TurnCompleted,
        OccurredAt = Start,
        TurnIndex = turnIndex,
        Payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AuditPayloadKeys.ReplyText] = "Welcome to Sole, how can I help you today?",
            [AuditPayloadKeys.StageBefore] = "greeting",
            [AuditPayloadKeys.StageAfter] = "identify",
        },
    };

    private static AuditEvent Interruption(long sequence, long amends, int turnIndex) => new()
    {
        CallId = "call-1",
        Sequence = sequence,
        Kind = AuditEventKind.ReplyInterrupted,
        OccurredAt = Start.AddMilliseconds(1_820),
        TurnIndex = turnIndex,
        AmendsSequence = amends,
        Payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AuditPayloadKeys.UtteranceUntilInterrupt] = "Welcome to Sole, how can I",
            [AuditPayloadKeys.DurationUntilInterruptMs] = "1820",
        },
    };

    private static AuditEvent Ended() => new()
    {
        CallId = "call-1",
        Sequence = 6,
        Kind = AuditEventKind.CallEnded,
        OccurredAt = Start.AddMilliseconds(9_000),
        Payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AuditPayloadKeys.EndReason] = CallEndReasons.ToToken(CallEndReason.CallerHungUp),
        },
    };
}

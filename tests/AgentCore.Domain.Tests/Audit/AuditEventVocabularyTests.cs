using AgentCore.Domain.Audit;
using Xunit;

namespace AgentCore.Domain.Tests.Audit;

/// <summary>
/// Both vocabularies are closed, their tokens are stable, and an amendment names the event it
/// amends.
/// </summary>
public sealed class AuditEventVocabularyTests
{
    /// <summary>What the model produced on the turn these facts describe.</summary>
    private const string Spoken = "Welcome to Sole, how can I help you today?";

    /// <summary>What the caller heard of it before speaking over the rest.</summary>
    private const string Heard = "Welcome to Sole, how can I";

    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    [Theory]
    [InlineData(AuditEventKind.CallStarted, "call.started")]
    [InlineData(AuditEventKind.TurnCompleted, "turn.completed")]
    [InlineData(AuditEventKind.ReplyInterrupted, "reply.interrupted")]
    [InlineData(AuditEventKind.ToolFailed, "tool.failed")]
    [InlineData(AuditEventKind.PromptFlagged, "prompt.flagged")]
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

        // Six kinds, and the numbers run 0 to 6 with 4 missing. Value 4 was reply.flagged, retired
        // on 2026-08-13 when reply moderation was withdrawn. A number is never reused, so a stored
        // row can never come to mean something else.
        Assert.Equal(6, declared.Length);
        Assert.DoesNotContain(declared, kind => (int)kind == 4);
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

    [Theory]
    [InlineData(ToolFailureKind.Undeclared, "tool.undeclared")]
    [InlineData(ToolFailureKind.Faulted, "tool.faulted")]
    public void EachToolFailureKind_HasItsToken(ToolFailureKind kind, string token)
    {
        // The token is stable forever, for the reason an end reason's is. A report that counts how
        // often the model invented a tool name reads this token years after the call.
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

        Assert.Throws<ArgumentOutOfRangeException>(() => ToolFailureKinds.ToToken((ToolFailureKind)99));
        Assert.False(ToolFailureKinds.TryParse("NotFound", out _));
        Assert.False(ToolFailureKinds.TryParse("Exception", out _));
        Assert.False(ToolFailureKinds.TryParse("1", out _));
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
            () => AuditEventVocabulary.Validate(free));

        Assert.Contains(AuditPayloadKeys.EndReason, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACallEndedEventWithoutAReason_IsRefused()
    {
        AuditEvent silent = Ended() with
        {
            Payload = new Dictionary<string, string>(StringComparer.Ordinal),
        };

        Assert.Throws<ArgumentException>(() => AuditEventVocabulary.Validate(silent));
    }

    /// <summary>
    /// T23: the table is append-only, so a barge-in is a second event that references the first.
    /// </summary>
    [Fact]
    public void AnAmendment_ReferencesTheEventItAmends()
    {
        AuditEvent turn = Turn(sequence: 4, turnIndex: 2);
        AuditEvent interruption = Interruption(sequence: 5, amends: turn.Sequence, turnIndex: 2);

        AuditEvent[] run = [turn, interruption];

        Assert.All(run, AuditEventVocabulary.Validate);
        Assert.Equal(turn.Sequence, interruption.AmendsSequence);
        Assert.Equal(turn.CallId, interruption.CallId);
        Assert.Equal(turn.TurnIndex, interruption.TurnIndex);

        // The first event is untouched. Nothing rewrote the turn, and both events stand.
        Assert.Equal(AuditHash.OfText(Spoken).Value, turn.Payload[AuditPayloadKeys.ReplyTextSha256]);
        Assert.Null(turn.AmendsSequence);
    }

    /// <summary>Section 11, item 6a: the event records the text the caller ACTUALLY HEARD.</summary>
    [Fact]
    public void AnInterruption_RecordsWhatTheCallerHeardAndNotWhatTheModelProduced()
    {
        AuditEvent turn = Turn(sequence: 0, turnIndex: 0);
        AuditEvent interruption = Interruption(sequence: 1, amends: 0, turnIndex: 0);

        string produced = turn.Payload[AuditPayloadKeys.ReplyTextSha256];
        string heard = interruption.Payload[AuditPayloadKeys.UtteranceUntilInterruptSha256];

        // The chain holds proof of the words and never the words, so the reviewer's check is against
        // store 1: the amendment proves what the caller heard, and it is not the whole reply.
        Assert.NotEqual(produced, heard);
        Assert.Equal(AuditHash.OfText(Heard).Value, heard);
        Assert.Equal(AuditHash.OfText(Spoken).Value, produced);
        Assert.Equal("1820", interruption.Payload[AuditPayloadKeys.DurationUntilInterruptMs]);
    }

    [Fact]
    public void AnInterruptionThatAmendsNothing_IsRefused()
    {
        AuditEvent orphan = Interruption(sequence: 1, amends: 0, turnIndex: 0) with { AmendsSequence = null };

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => AuditEventVocabulary.Validate(orphan));

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
            () => AuditEventVocabulary.Validate(silent));

        Assert.Contains(AuditPayloadKeys.UtteranceUntilInterruptSha256, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_EmptyHashOnInterrupt_IsRefused()
    {
        // The chain stores proof of the words and not the words, so the one value that must never
        // reach it is a hash that proves nothing. An empty text still hashes to a full digest.
        AuditEvent unproven = Interruption(sequence: 1, amends: 0, turnIndex: 0) with
        {
            Payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.UtteranceUntilInterruptSha256] = string.Empty,
                [AuditPayloadKeys.DurationUntilInterruptMs] = "1820",
            },
        };

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => AuditEventVocabulary.Validate(unproven));

        Assert.Contains(AuditPayloadKeys.UtteranceUntilInterruptSha256, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_EmptyHashOnTurnCompleted_IsRefused()
    {
        AuditEvent unproven = Turn(sequence: 0, turnIndex: 0) with
        {
            Payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.ReplyTextSha256] = string.Empty,
            },
        };

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => AuditEventVocabulary.Validate(unproven));

        Assert.Contains(AuditPayloadKeys.ReplyTextSha256, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAmendmentThatNamesALaterEvent_IsRefused()
    {
        AuditEvent backwards = Interruption(sequence: 1, amends: 0, turnIndex: 0) with { AmendsSequence = 9 };

        Assert.Throws<ArgumentException>(() => AuditEventVocabulary.Validate(backwards));
    }

    [Fact]
    public void AnEventWithoutACallId_IsRefused()
    {
        AuditEvent nameless = Turn(sequence: 0, turnIndex: 0) with { CallId = string.Empty };

        Assert.Throws<ArgumentException>(() => AuditEventVocabulary.Validate(nameless));
    }

    [Fact]
    public void AHash_RefusesEverySpellingButLowercaseHexadecimal()
    {
        // PostgreSQL renders sha256() through encode(..., 'hex'), which is lowercase.
        Assert.Throws<ArgumentException>(() => AuditHash.Parse(new string('A', AuditHash.Length)));
        Assert.Throws<ArgumentException>(() => AuditHash.Parse(new string('0', AuditHash.Length - 1)));
        Assert.Throws<ArgumentException>(() => AuditHash.Parse("not a hash"));
        Assert.False(AuditHash.TryParse(null, out _));
    }

    /// <summary>
    /// The moderation verdict is known BEFORE the model runs, so the event amends nothing. This is
    /// the rule that differs from <c>reply.interrupted</c>, and it is the reason the two kinds are
    /// two kinds.
    /// </summary>
    [Fact]
    public void AFlaggedPrompt_NeedsNoAmendment()
    {
        AuditEvent flagged = FlaggedPrompt(sequence: 1, turnIndex: 1);

        Assert.Null(flagged.AmendsSequence);
        Assert.Equal(1, flagged.TurnIndex);

        AuditEvent[] run = [flagged];

        Assert.All(run, AuditEventVocabulary.Validate);
    }

    /// <summary>
    /// The agent refuses the turn, so the flag is written before the turn event that closes it.
    /// <c>TurnIndex</c> names the turn, and no amendment is involved.
    /// </summary>
    [Fact]
    public void AFlaggedPrompt_SitsBeforeTheTurnItBelongsTo()
    {
        AuditEvent flagged = FlaggedPrompt(sequence: 3, turnIndex: 1);
        AuditEvent turn = Turn(sequence: 4, turnIndex: 1);

        AuditEvent[] run = [flagged, turn];

        Assert.All(run, AuditEventVocabulary.Validate);
        Assert.True(flagged.Sequence < turn.Sequence);
        Assert.Equal(flagged.TurnIndex, turn.TurnIndex);
        Assert.Null(flagged.AmendsSequence);
    }

    /// <summary>
    /// The kind alone says something flagged the caller. The categories are the only other fact the
    /// event holds, and §9 makes the chain the only long-term record.
    /// </summary>
    [Fact]
    public void AFlaggedPromptWithoutTheCategories_IsRefused()
    {
        AuditEvent silent = FlaggedPrompt(sequence: 1, turnIndex: 1) with
        {
            Payload = new Dictionary<string, string>(StringComparer.Ordinal),
        };

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => AuditEventVocabulary.Validate(silent));

        Assert.Contains(AuditPayloadKeys.ModerationCategories, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlaggedPromptWithAnEmptyCategoryList_IsRefused()
    {
        AuditEvent empty = FlaggedPrompt(sequence: 1, turnIndex: 1, categories: string.Empty);

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => AuditEventVocabulary.Validate(empty));

        Assert.Contains(AuditPayloadKeys.ModerationCategories, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A reader splits on a comma and counts, and a blank member makes the count wrong.</summary>
    [Theory]
    [InlineData("harassment,,violence")]
    [InlineData("harassment,")]
    [InlineData(",harassment")]
    public void AFlaggedPromptWithABlankCategory_IsRefused(string categories)
    {
        AuditEvent blank = FlaggedPrompt(sequence: 1, turnIndex: 1, categories: categories);

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => AuditEventVocabulary.Validate(blank));

        Assert.Contains(AuditPayloadKeys.ModerationCategories, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The category list is stored exactly as the endpoint returned it. Nothing sorts it, and
    /// nothing rewrites it.
    /// </summary>
    /// <remarks>
    /// The order carries meaning the taxonomy does not: the endpoint returns its own ranking, and a
    /// reader years later has only this row. Validation checks the SHAPE of the list and never the
    /// names in it, so both spellings below are legal and neither is normalised.
    /// </remarks>
    [Fact]
    public void AFlaggedPromptKeepsTheOrderTheEndpointReturned()
    {
        AuditEvent first = FlaggedPrompt(sequence: 1, turnIndex: 1, categories: "harassment,violence");
        AuditEvent second = FlaggedPrompt(sequence: 1, turnIndex: 1, categories: "violence,harassment");

        AuditEventVocabulary.Validate(first);
        AuditEventVocabulary.Validate(second);

        Assert.Equal("harassment,violence", first.Payload[AuditPayloadKeys.ModerationCategories]);
        Assert.Equal("violence,harassment", second.Payload[AuditPayloadKeys.ModerationCategories]);
    }

    /// <summary>
    /// The taxonomy belongs to the moderation endpoint and it is open, unlike
    /// <see cref="CallEndReason"/>. A closed set would make <see cref="AuditEventVocabulary.Validate"/> throw on a
    /// category OpenAI added, and destroy the record the chain exists to protect.
    /// </summary>
    [Fact]
    public void ACategoryTheLibraryNeverNamed_IsAccepted()
    {
        AuditEvent novel = FlaggedPrompt(
            sequence: 1,
            turnIndex: 1,
            categories: "illicit/violent,some-category-openai-added-last-tuesday");

        AuditEventVocabulary.Validate(novel);

        Assert.Equal(
            "illicit/violent,some-category-openai-added-last-tuesday",
            novel.Payload[AuditPayloadKeys.ModerationCategories]);
    }

    /// <summary>The rule requires no amendment, and it forbids none either.</summary>
    [Fact]
    public void AFlaggedPrompt_MayStillCarryAnAmendment()
    {
        AuditEvent turn = Turn(sequence: 4, turnIndex: 1);
        AuditEvent flagged = FlaggedPrompt(sequence: 5, turnIndex: 1) with { AmendsSequence = turn.Sequence };

        AuditEvent[] run = [turn, flagged];

        Assert.All(run, AuditEventVocabulary.Validate);
        Assert.Equal(turn.Sequence, flagged.AmendsSequence);
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
            [AuditPayloadKeys.ReplyTextSha256] = AuditHash.OfText(Spoken).Value,
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
            [AuditPayloadKeys.UtteranceUntilInterruptSha256] = AuditHash.OfText(Heard).Value,
            [AuditPayloadKeys.DurationUntilInterruptMs] = "1820",
        },
    };

    private static AuditEvent FlaggedPrompt(long sequence, int turnIndex, string categories = "harassment") => new()
    {
        CallId = "call-1",
        Sequence = sequence,
        Kind = AuditEventKind.PromptFlagged,
        OccurredAt = Start.AddMilliseconds(2_400),
        TurnIndex = turnIndex,
        Payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AuditPayloadKeys.ModerationCategories] = categories,
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

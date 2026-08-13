using AgentCore.Domain.Audit;
using Xunit;

namespace AgentCore.Domain.Tests.Audit;

/// <summary>
/// The chain links, hashes, and verifies. It is <c>chain_check</c> outside the database.
/// </summary>
public sealed class AuditChainTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    [Fact]
    public void AChainOfSeveralEvents_VerifiesOk()
    {
        IReadOnlyList<AuditChainLink> links = AuditChain.LinkAll(OneCall());

        AuditChainVerification verification = AuditChain.Verify(links);

        Assert.True(verification.IsIntact);
        Assert.Equal(AuditChainVerification.OkResult, verification.Result);
        Assert.Equal(-1, verification.BrokenLinkIndex);
        Assert.Null(verification.Reason);
    }

    [Fact]
    public void TheFirstLink_StandsOnGenesis()
    {
        IReadOnlyList<AuditChainLink> links = AuditChain.LinkAll(OneCall());

        Assert.Equal(AuditHash.Genesis, links[0].PreviousHash);
        for (int index = 1; index < links.Count; index++)
        {
            Assert.Equal(links[index - 1].Hash, links[index].PreviousHash);
        }
    }

    [Fact]
    public void AnEmptyChain_VerifiesOk()
    {
        // A call that never started is not a call that was tampered with.
        Assert.True(AuditChain.Verify([]).IsIntact);
    }

    [Fact]
    public void AnEditedPayload_BreaksTheChainAtThatLink()
    {
        List<AuditChainLink> links = [.. AuditChain.LinkAll(OneCall())];

        // Someone rewrites what the caller heard, and leaves every stored hash alone.
        AuditEvent tampered = links[2].Event with
        {
            Payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.UtteranceUntilInterrupt] = "Welcome to Sole, how can I help you today?",
                [AuditPayloadKeys.DurationUntilInterruptMs] = "1820",
            },
        };
        links[2] = links[2] with { Event = tampered };

        AuditChainVerification verification = AuditChain.Verify(links);

        Assert.False(verification.IsIntact);
        Assert.Equal(AuditChainVerification.LinkBrokenResult, verification.Result);
        Assert.Equal(2, verification.BrokenLinkIndex);
        Assert.NotNull(verification.Reason);
    }

    [Fact]
    public void ARemovedLink_BreaksTheChainWhereItWasRemoved()
    {
        List<AuditChainLink> links = [.. AuditChain.LinkAll(OneCall())];
        links.RemoveAt(1);

        AuditChainVerification verification = AuditChain.Verify(links);

        Assert.False(verification.IsIntact);
        Assert.Equal(1, verification.BrokenLinkIndex);
    }

    [Fact]
    public void ASwappedHash_BreaksTheChainAtThatLink()
    {
        List<AuditChainLink> links = [.. AuditChain.LinkAll(OneCall())];
        links[3] = links[3] with { Hash = AuditHash.Genesis };

        AuditChainVerification verification = AuditChain.Verify(links);

        Assert.False(verification.IsIntact);
        Assert.Equal(3, verification.BrokenLinkIndex);
    }

    [Fact]
    public void TheSameEventAfterADifferentPredecessor_HashesDifferently()
    {
        AuditEvent auditEvent = CallStarted();

        AuditHash first = AuditChain.ComputeHash(auditEvent, AuditHash.Genesis);
        AuditHash second = AuditChain.ComputeHash(auditEvent, AuditChain.ComputeHash(auditEvent, AuditHash.Genesis));

        Assert.NotEqual(first, second);
    }

    /// <summary>The turn loop and the sink build the event twice and must agree.</summary>
    [Fact]
    public void TwoIndependentConstructionsOfTheSameEvent_ProduceTheSameCanonicalForm()
    {
        AuditEvent built = new()
        {
            CallId = "call-1",
            Sequence = 7,
            Kind = AuditEventKind.TurnCompleted,
            OccurredAt = Start.AddMilliseconds(4_000),
            TurnIndex = 3,
            Payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.ReplyText] = "The torque is 45 newton metres.",
                [AuditPayloadKeys.StageBefore] = "resolve",
                [AuditPayloadKeys.StageAfter] = "close",
            },
        };

        // The same facts, added in another order, through another dictionary, in another time zone.
        AuditEvent rebuilt = new()
        {
            CallId = "call-1",
            Sequence = 7,
            Kind = AuditEventKind.TurnCompleted,
            OccurredAt = Start.AddMilliseconds(4_000).ToOffset(TimeSpan.FromHours(8)),
            TurnIndex = 3,
            Payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.StageAfter] = "close",
                [AuditPayloadKeys.ReplyText] = "The torque is 45 newton metres.",
                [AuditPayloadKeys.StageBefore] = "resolve",
            },
        };

        Assert.Equal(
            AuditChain.Canonicalize(built, AuditHash.Genesis),
            AuditChain.Canonicalize(rebuilt, AuditHash.Genesis));
        Assert.Equal(
            AuditChain.ComputeHash(built, AuditHash.Genesis),
            AuditChain.ComputeHash(rebuilt, AuditHash.Genesis));
    }

    /// <summary>
    /// T56 recomputes this form inside PostgreSQL, so the exact bytes are pinned here. A change to
    /// this text is a change to the canonical form, and it needs a new
    /// <see cref="AuditChain.CanonicalFormVersion"/>.
    /// </summary>
    [Fact]
    public void TheCanonicalForm_IsExactlyThis()
    {
        string canonical = AuditChain.Canonicalize(CallStarted(), AuditHash.Genesis);

        Assert.Equal(
            "agentcore-audit-v1\n"
            + "64:0000000000000000000000000000000000000000000000000000000000000000\n"
            + "6:call-1\n"
            + "1:0\n"
            + "12:call.started\n"
            + "13:1700000000000\n"
            + "0:\n"
            + "0:\n"
            + "1:0\n",
            canonical);
    }

    /// <summary>
    /// The prefix counts UTF-8 bytes, so PostgreSQL's <c>octet_length</c> reproduces it. A count of
    /// characters would disagree the first time a caller says something outside the basic
    /// multilingual plane.
    /// </summary>
    [Fact]
    public void TheLengthPrefix_CountsUtf8BytesAndNotCharacters()
    {
        AuditEvent auditEvent = CallStarted() with
        {
            Payload = new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "\U0001F600" },
        };

        string canonical = AuditChain.Canonicalize(auditEvent, AuditHash.Genesis);

        // Two UTF-16 code units, four UTF-8 bytes.
        Assert.Contains("4:\U0001F600\n", canonical, StringComparison.Ordinal);
    }

    /// <summary>Free text holds line feeds and colons, and the length prefix needs no escape for them.</summary>
    [Fact]
    public void APayloadValueWithADelimiter_DoesNotCollideWithAnotherEvent()
    {
        AuditEvent awkward = CallStarted() with
        {
            Payload = new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "1\n2:b\n" },
        };
        AuditEvent plain = CallStarted() with
        {
            Payload = new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "1", ["2"] = "b" },
        };

        Assert.NotEqual(
            AuditChain.ComputeHash(awkward, AuditHash.Genesis),
            AuditChain.ComputeHash(plain, AuditHash.Genesis));
    }

    [Fact]
    public void ASubMillisecondDifference_DoesNotChangeTheHash()
    {
        // The canonical form keeps Unix milliseconds. Sequence orders two events inside one millisecond.
        AuditEvent first = CallStarted();
        AuditEvent second = first with { OccurredAt = first.OccurredAt.AddTicks(9_999) };

        Assert.Equal(
            AuditChain.ComputeHash(first, AuditHash.Genesis),
            AuditChain.ComputeHash(second, AuditHash.Genesis));
    }

    [Fact]
    public void AHash_IsSixtyFourLowercaseHexadecimalCharacters()
    {
        AuditHash hash = AuditChain.ComputeHash(CallStarted(), AuditHash.Genesis);

        Assert.Equal(AuditHash.Length, hash.Value.Length);
        Assert.Equal(hash.Value.ToLowerInvariant(), hash.Value);
        Assert.Equal(hash, AuditHash.Parse(hash.Value));
    }

    private static AuditEvent CallStarted() => new()
    {
        CallId = "call-1",
        Sequence = 0,
        Kind = AuditEventKind.CallStarted,
        OccurredAt = Start,
    };

    /// <summary>One call: it starts, one turn runs, the caller barges in, a tool fails, and it ends.</summary>
    private static IReadOnlyList<AuditEvent> OneCall() =>
    [
        CallStarted(),
        new AuditEvent
        {
            CallId = "call-1",
            Sequence = 1,
            Kind = AuditEventKind.TurnCompleted,
            OccurredAt = Start.AddMilliseconds(1_200),
            TurnIndex = 0,
            Payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.ReplyText] = "Welcome to Sole, how can I help you today?",
                [AuditPayloadKeys.StageBefore] = "greeting",
                [AuditPayloadKeys.StageAfter] = "identify",
            },
        },
        new AuditEvent
        {
            CallId = "call-1",
            Sequence = 2,
            Kind = AuditEventKind.ReplyInterrupted,
            OccurredAt = Start.AddMilliseconds(3_020),
            TurnIndex = 0,
            AmendsSequence = 1,
            Payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.UtteranceUntilInterrupt] = "Welcome to Sole, how can I",
                [AuditPayloadKeys.DurationUntilInterruptMs] = "1820",
            },
        },
        new AuditEvent
        {
            CallId = "call-1",
            Sequence = 3,
            Kind = AuditEventKind.ToolFailed,
            OccurredAt = Start.AddMilliseconds(5_400),
            TurnIndex = 1,
            Payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.ToolName] = "lookup_order",
                [AuditPayloadKeys.ToolError] = "the endpoint answered 503",
            },
        },
        // Turn 2 never reached the model: moderation flagged what the caller said, the agent spoke
        // the refusal line, and the call ended. The flag amends nothing, because the verdict is
        // known before the model runs.
        new AuditEvent
        {
            CallId = "call-1",
            Sequence = 4,
            Kind = AuditEventKind.PromptFlagged,
            OccurredAt = Start.AddMilliseconds(6_100),
            TurnIndex = 2,
            Payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.ModerationCategories] = "harassment",
            },
        },
        new AuditEvent
        {
            CallId = "call-1",
            Sequence = 5,
            Kind = AuditEventKind.CallEnded,
            OccurredAt = Start.AddMilliseconds(9_000),
            Payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.EndReason] = CallEndReasons.ToToken(CallEndReason.CallerHungUp),
            },
        },
    ];
}

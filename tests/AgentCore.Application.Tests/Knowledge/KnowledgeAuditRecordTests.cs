using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Domain.Knowledge;
using Xunit;

namespace AgentCore.Application.Tests.Knowledge;

/// <summary>
/// The only place a Qdrant outage's real cause survives once tool mode discards it.
/// </summary>
public sealed class KnowledgeAuditRecordTests
{
    [Fact]
    public void Cards_DistinguishRankedFromLinked()
    {
        // `via` is the mechanism the probes proved is required for correctness, three times over.
        // It is therefore the thing that will need debugging.
        var record = KnowledgeAuditRecord.For("turn", "resolver", KnowledgeMode.Prefetch, "e33", Scope(),
            [Ranked("a", 0.87), Linked("b")], latencyMs: 106, failure: null);

        Assert.Equal("ranked", record.Cards[0].Via);
        Assert.Equal("see_also", record.Cards[1].Via);
        Assert.Null(record.Cards[1].Score);
    }

    [Fact]
    public void For_ReadsTheWholeRetrievalLatencyAsOneNumber()
    {
        // Ruling 19: IKnowledgeRetrievalPort is one atomic method, so embed time and search time
        // are not separately observable above it. There is one field, not two.
        var record = KnowledgeAuditRecord.For("turn", "resolver", KnowledgeMode.Prefetch, "e33", Scope(),
            [], latencyMs: 106, failure: null);

        Assert.Equal(106, record.LatencyMs);
    }

    [Fact]
    public void For_AFailedRetrieval_KeepsTheRealCause()
    {
        // In tool mode the framework discards the exception message. This is the only place it survives.
        var record = KnowledgeAuditRecord.For("turn", "resolver", KnowledgeMode.Tool, "e33", Scope(),
            [], latencyMs: 92, failure: new InvalidOperationException("qdrant is down"));

        Assert.Contains("qdrant is down", record.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void CardEntry_CarriesEveryFieldTheCitationIsRebuiltFrom()
    {
        // Only Via and Score were checked before, so a mapping mistake in the other four would
        // survive: an engineer reading the record would be shown the wrong manual, at the wrong page,
        // for the wrong card, with nothing looking broken.
        var record = KnowledgeAuditRecord.For("turn", "resolver", KnowledgeMode.Prefetch, "e33", Scope(),
            [Ranked("ct900-e33-incline-err", 0.87)], latencyMs: 106, failure: null);

        var card = Assert.Single(record.Cards);
        Assert.Equal("ct900-e33-incline-err", card.CardId);
        Assert.Equal(3, card.Authority);
        Assert.Equal("ct900-om", card.SourceRef);
        Assert.Equal("p.27", card.Locator);
        Assert.Equal(0.87, card.Score);
    }

    [Fact]
    public void For_CarriesEveryFieldTheOutageIsDiagnosedFrom()
    {
        var record = KnowledgeAuditRecord.For("turn-7", "analyst", KnowledgeMode.Tool, "belt slipping", Scope(),
            [], latencyMs: 106, failure: null);

        Assert.Equal("turn-7", record.TurnId);
        Assert.Equal("analyst", record.Agent);
        Assert.Equal(KnowledgeMode.Tool, record.Mode);
        Assert.Equal("belt slipping", record.Query);
        Assert.Equal("ct900", record.Scope["model"]);
    }

    [Fact]
    public void For_NoTurnIdIsReachable_LeavesTheFieldEmptyRatherThanInventingOne()
    {
        // Ruling 19, then Ruling 21. The provider that writes this record can reach no call id and no
        // turn index, so it passes null. A synthesised id would read as real to whoever greps for it.
        var record = KnowledgeAuditRecord.For(turnId: null, "analyst", KnowledgeMode.Tool, "e33", scope: null,
            [], latencyMs: 106, failure: null);

        Assert.Null(record.TurnId);
        Assert.Empty(record.Scope);
    }

    [Fact]
    public void CardEntry_CardWithNoAuthorityOrSource_RecordsNulls()
    {
        var card = new KnowledgeCard { CardId = "plain-01", Text = "a card", ViaLink = false };

        var entry = KnowledgeAuditRecord.CardEntry.For(card);

        Assert.Equal("plain-01", entry.CardId);
        Assert.Null(entry.Authority);
        Assert.Equal(string.Empty, entry.SourceRef);
        Assert.Equal(string.Empty, entry.Locator);
        Assert.Equal("ranked", entry.Via);
    }

    private static KnowledgeScope Scope()
        => new() { Facets = new Dictionary<string, string> { ["model"] = "ct900" } };

    private static KnowledgeCard Ranked(string id, double score)
        => new()
        {
            CardId = id,
            Text = "card " + id,
            Authority = 3,
            SourceRef = "ct900-om",
            SourceLocator = "p.27",
            Score = score,
            ViaLink = false,
        };

    private static KnowledgeCard Linked(string id)
        => Ranked(id, 0) with { Score = null, ViaLink = true };
}

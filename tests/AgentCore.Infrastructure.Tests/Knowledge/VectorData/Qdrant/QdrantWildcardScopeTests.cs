using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Knowledge;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using AgentCore.Infrastructure.Tests.Fakes;
using AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant.Fakes;
using Qdrant.Client.Grpc;
using Xunit;
using static AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant.TestScopes;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>
/// The store's Qdrant filter under <c>scope.wildcard</c>: a named facet widens to
/// <c>[value, wildcard]</c>, every other facet stays an exact match, and a card reached only
/// through a link is filtered by the same rule the query used.
/// </summary>
/// <remarks>
/// <see cref="Search_FacetNotNamed_StaysExactEvenBesideANamedOne"/> is the tenant-isolation case: a
/// wildcard configured for one facet must never leak into an unnamed one sitting beside it in the
/// same scope.
/// </remarks>
public sealed class QdrantWildcardScopeTests
{
    [Fact]
    public async Task Search_NoWildcardConfigured_EmitsTheKeywordCase()
    {
        var channel = new CapturingSearchChannel([]);
        var store = StoreOver(channel, wildcard: null, wildcardFacets: []);

        using (KnowledgeScopeScope.Open(Scope(("brand", "sole"))))
        {
            await store.SearchAsync("belt", TestContext.Current.CancellationToken);
        }

        var condition = channel.Query!.Prefetch[0].Filter.Must[0].Field;
        Assert.Equal(Match.MatchValueOneofCase.Keyword, condition.Match.MatchValueCase);
        Assert.Equal("sole", condition.Match.Keyword);
    }

    [Fact]
    public async Task Search_NamedFacet_EmitsBothTheValueAndTheWildcard()
    {
        var channel = new CapturingSearchChannel([]);
        var store = StoreOver(channel, wildcard: "*", wildcardFacets: ["brand"]);

        using (KnowledgeScopeScope.Open(Scope(("brand", "sole"))))
        {
            await store.SearchAsync("belt", TestContext.Current.CancellationToken);
        }

        var match = channel.Query!.Prefetch[0].Filter.Must[0].Field.Match;
        Assert.Equal(Match.MatchValueOneofCase.Keywords, match.MatchValueCase);
        Assert.Equal(["*", "sole"], match.Keywords.Strings);
    }

    [Fact]
    public async Task Search_FacetNotNamed_StaysExactEvenBesideANamedOne()
    {
        var channel = new CapturingSearchChannel([]);
        var store = StoreOver(channel, wildcard: "*", wildcardFacets: ["brand"]);

        using (KnowledgeScopeScope.Open(Scope(("brand", "sole"), ("customer_id", "c-91"))))
        {
            await store.SearchAsync("belt", TestContext.Current.CancellationToken);
        }

        var conditions = channel.Query!.Prefetch[0].Filter.Must;
        var tenant = conditions.Single(c => c.Field.Key.EndsWith("customer_id", StringComparison.Ordinal));
        Assert.Equal(Match.MatchValueOneofCase.Keyword, tenant.Field.Match.MatchValueCase);
    }

    [Fact]
    public async Task Search_ValueEqualsTheWildcard_EmitsOneKeyword()
    {
        var channel = new CapturingSearchChannel([]);
        var store = StoreOver(channel, wildcard: "*", wildcardFacets: ["brand"]);

        using (KnowledgeScopeScope.Open(Scope(("brand", "*"))))
        {
            await store.SearchAsync("belt", TestContext.Current.CancellationToken);
        }

        var match = channel.Query!.Prefetch[0].Filter.Must[0].Field.Match;
        Assert.Equal(Match.MatchValueOneofCase.Keyword, match.MatchValueCase);
        Assert.Equal("*", match.Keyword);
    }

    [Fact]
    public async Task Search_LinkedCardTaggedWildcard_IsKept()
    {
        // Linked cards are fetched by id and never pass the Qdrant filter, so InScope is the only
        // gate on them. It has to accept the same set the query did.
        var store = StoreOver(
            ChannelWithLink(linkedFacet: "*"), wildcard: "*", wildcardFacets: ["brand"], withLinks: true);

        using (KnowledgeScopeScope.Open(Scope(("brand", "sole"))))
        {
            var cards = await store.SearchAsync("belt", TestContext.Current.CancellationToken);
            Assert.Contains(cards, card => card.ViaLink);
        }
    }

    [Fact]
    public async Task Search_LinkedCardOutsideTheScope_IsDropped()
    {
        var store = StoreOver(
            ChannelWithLink(linkedFacet: "spirit"), wildcard: "*", wildcardFacets: ["brand"], withLinks: true);

        using (KnowledgeScopeScope.Open(Scope(("brand", "sole"))))
        {
            var cards = await store.SearchAsync("belt", TestContext.Current.CancellationToken);
            Assert.DoesNotContain(cards, card => card.ViaLink);
        }
    }

    /// <summary>A ranked point linking to one fetched point tagged <paramref name="linkedFacet"/> on <c>brand</c>.</summary>
    private static CapturingSearchChannel ChannelWithLink(string linkedFacet)
    {
        var linkedId = Guid.NewGuid();

        var ranked = new ScoredPoint { Id = new PointId { Uuid = Guid.NewGuid().ToString() }, Score = 1f };
        ranked.Payload["card_id"] = "card-1";
        ranked.Payload["body"] = "text";
        ranked.Payload["see_also"] = new Value
        {
            ListValue = new ListValue { Values = { new Value { StringValue = linkedId.ToString() } } },
        };

        var fetched = new RetrievedPoint { Id = new PointId { Uuid = linkedId.ToString() } };
        fetched.Payload["card_id"] = "card-linked";
        fetched.Payload["body"] = "linked text";
        fetched.Payload["brand"] = linkedFacet;

        return new CapturingSearchChannel([ranked], [fetched]);
    }

    private static QdrantKnowledgeStore StoreOver(
        CapturingSearchChannel channel,
        string? wildcard,
        IReadOnlyList<string> wildcardFacets,
        bool withLinks = false) => new(
        channel,
        new FakeEmbeddingGenerator([1f]),
        new QdrantKnowledgeStoreOptions
        {
            Collection = "anything",
            Scoped = true,
            ScopeTemplate = "{key}",
            Fields = new KnowledgeFieldsConfiguration { Id = "card_id", Body = "body" },
            ScoreFloor = 0.0,
            ScopeWildcard = wildcard,
            ScopeWildcardFacets = wildcardFacets,
            Links = withLinks
                ? new KnowledgeLinksConfiguration { Field = "see_also", Lookup = KnowledgeLinkLookup.Direct }
                : null,
        });
}

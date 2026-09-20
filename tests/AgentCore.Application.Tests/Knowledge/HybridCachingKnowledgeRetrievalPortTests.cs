using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.Application.Tests.Knowledge;

/// <summary>
/// The cache in front of the knowledge store. A repeat of one query under one scope must not
/// reach the store again; a different query or a different facet must. What comes back must be
/// the card the store gave, payload tree included.
/// </summary>
public sealed class HybridCachingKnowledgeRetrievalPortTests
{
    private static readonly KnowledgeCard Card = new()
    {
        CardId = "card-1",
        Text = "Loosen the four bolts.",
        ViaLink = false,
        SourceRef = "manual-f85",
        SourceLocator = "p.12",
        Authority = 3,
        Score = 0.91,
        Extras = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = "f85-2019",
            ["page"] = 12L,
            ["weight"] = 1.5,
            ["draft"] = false,
            ["tags"] = (IReadOnlyList<object?>)["belt", "deck"],
            ["source"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["doc"] = "owner-manual" },
        },
    };

    private static HybridCache NewCache()
    {
        ServiceCollection services = new();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    private static KnowledgeScope Scope(params (string Key, string Value)[] facets) => new()
    {
        Facets = facets.ToDictionary(facet => facet.Key, facet => facet.Value, StringComparer.Ordinal),
    };

    [Fact]
    public async Task Repeat_of_one_query_skips_the_store()
    {
        StubKnowledgePort store = new([Card]);
        HybridCachingKnowledgeRetrievalPort port = new(store, NewCache());

        await port.SearchAsync("belt slips", cancellationToken: TestContext.Current.CancellationToken);
        await port.SearchAsync("belt slips", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task A_cached_card_reads_back_whole()
    {
        StubKnowledgePort store = new([Card]);
        HybridCachingKnowledgeRetrievalPort port = new(store, NewCache());

        await port.SearchAsync("belt slips", cancellationToken: TestContext.Current.CancellationToken);
        var cards = await port.SearchAsync("belt slips", cancellationToken: TestContext.Current.CancellationToken);

        var card = Assert.Single(cards);
        Assert.Equal(Card with { Extras = card.Extras }, card);
        Assert.Equal("f85-2019", PayloadPath.Read(card.Extras, "model"));
        Assert.Equal(12L, PayloadPath.Read(card.Extras, "page"));
        Assert.Equal(1.5, PayloadPath.Read(card.Extras, "weight"));
        Assert.False(Assert.IsType<bool>(PayloadPath.Read(card.Extras, "draft")));
        Assert.Equal("owner-manual", PayloadPath.Read(card.Extras, "source.doc"));
        Assert.Equal(["belt", "deck"], Assert.IsType<IReadOnlyList<object?>>(PayloadPath.Read(card.Extras, "tags"), exactMatch: false));
    }

    [Fact]
    public async Task A_different_facet_value_reaches_the_store()
    {
        StubKnowledgePort store = new([Card]);
        HybridCachingKnowledgeRetrievalPort port = new(store, NewCache());

        await port.SearchAsync("belt slips", Scope(("model", "f85")), TestContext.Current.CancellationToken);
        await port.SearchAsync("belt slips", Scope(("model", "f63")), TestContext.Current.CancellationToken);

        Assert.Equal(2, store.Calls);
    }

    [Fact]
    public async Task Facet_order_does_not_change_the_key()
    {
        StubKnowledgePort store = new([Card]);
        HybridCachingKnowledgeRetrievalPort port = new(store, NewCache());

        await port.SearchAsync("belt slips", Scope(("model", "f85"), ("code", "us")), TestContext.Current.CancellationToken);
        await port.SearchAsync("belt slips", Scope(("code", "us"), ("model", "f85")), TestContext.Current.CancellationToken);

        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task A_store_that_is_down_caches_nothing()
    {
        ThrowingKnowledgePort store = new(new InvalidOperationException("down"));
        HybridCachingKnowledgeRetrievalPort port = new(store, NewCache());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => port.SearchAsync("belt slips", cancellationToken: TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => port.SearchAsync("belt slips", cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public void A_capability_of_the_store_is_reachable_through_the_cache()
    {
        FacetCapablePort store = new();
        HybridCachingKnowledgeRetrievalPort port = new(store, NewCache());

        Assert.Same(store, port.GetService<IKnowledgeFacetReadPort>());
        Assert.Same(port, port.GetService<IKnowledgeRetrievalPort>());
    }

    [Fact]
    public async Task Closing_the_cache_closes_the_store()
    {
        DisposingPort store = new();
        HybridCachingKnowledgeRetrievalPort port = new(store, NewCache());

        await port.DisposeAsync();

        Assert.True(store.Closed);
    }

    private sealed class FacetCapablePort : IKnowledgeRetrievalPort, IKnowledgeFacetReadPort
    {
        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, KnowledgeScope? scope = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);

        public ValueTask<IReadOnlyList<KnowledgeCard>> ReadByFacetAsync(
            string path, string value, int limit, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);
    }

    private sealed class DisposingPort : IKnowledgeRetrievalPort, IDisposable
    {
        public bool Closed { get; private set; }

        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, KnowledgeScope? scope = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);

        public void Dispose() => Closed = true;
    }
}

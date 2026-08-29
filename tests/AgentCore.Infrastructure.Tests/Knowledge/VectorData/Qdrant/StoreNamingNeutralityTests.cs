using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Knowledge;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using AgentCore.Infrastructure.Tests.Fakes;
using AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant.Fakes;
using Qdrant.Client.Grpc;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>
/// Every payload name the store uses comes from the document, and none from the framework.
/// </summary>
/// <remarks>
/// <para>
/// These run with no Qdrant. That is the point: the neutrality proof used to live entirely in
/// <see cref="ForeignSchemaTests"/>, which is <c>[QdrantFact]</c> and therefore SKIPS on a machine
/// with no server. A green <c>dotnet test</c> proved nothing about naming at all.
/// </para>
/// <para>
/// They also assert the requests themselves rather than the rows that come back. A live query that
/// returns the right rows is consistent with the store having guessed a key correctly; a captured
/// <c>FieldCondition</c> naming <c>region</c> is not.
/// </para>
/// </remarks>
public sealed class StoreNamingNeutralityTests
{
    /// <summary>The one collection these tests describe: nothing shared with any other corpus.</summary>
    private static KnowledgeFieldsConfiguration Foreign => new()
    {
        Id = "doc_id",
        Body = "content",
        Lexical = "content",
        Source = "origin",
        Locator = "page",
        Authority = "trust",
    };

    [Fact]
    public async Task Search_ReadsEveryRoleFromThePathTheDocumentNamed()
    {
        var channel = new CapturingSearchChannel([Point()]);

        var card = Assert.Single(await Store(channel).SearchAsync("anything", TestContext.Current.CancellationToken));

        Assert.Equal("DOC-01", card.CardId);
        Assert.Equal("the body text", card.Text);
        Assert.Equal("handbook-7", card.SourceRef);
        Assert.Equal("s.4", card.SourceLocator);
        Assert.Equal(2, card.Authority);
    }

    [Fact]
    public async Task Search_AScopeFacet_BecomesThePathTheTemplateNames()
    {
        // The template is the whole contract. "facets.{key}" and "{key}" address different payloads,
        // and a store that picked one would silently match nothing against the other.
        var channel = new CapturingSearchChannel([Point()]);

        using (KnowledgeScopeScope.Open(Scope("region", "emea")))
        {
            await Store(channel, template: "{key}", scoped: true)
                .SearchAsync("anything", TestContext.Current.CancellationToken);
        }

        Assert.Equal("emea", channel.DenseFilterKeywords["region"]);
    }

    [Fact]
    public async Task Search_ANestedTemplate_BecomesADottedPath()
    {
        var channel = new CapturingSearchChannel([Point()]);

        using (KnowledgeScopeScope.Open(Scope("region", "emea")))
        {
            await Store(channel, template: "attributes.{key}", scoped: true)
                .SearchAsync("anything", TestContext.Current.CancellationToken);
        }

        Assert.Equal("emea", channel.DenseFilterKeywords["attributes.region"]);
        Assert.False(channel.DenseFilterKeywords.ContainsKey("facets.region"));
    }

    [Fact]
    public async Task Search_TheRequiredTermLeg_MatchesOnTheMappedLexicalField()
    {
        var channel = new CapturingSearchChannel([Point()]);

        await Store(channel, analyzer: new IdentifierCodeAnalyzer())
            .SearchAsync("the screen says e33", TestContext.Current.CancellationToken);

        Assert.Equal("content", Assert.Single(channel.LexicalKeys));
    }

    [Fact]
    public async Task Search_NoLexicalRoleMapped_SendsOneLegAndNoTextCondition()
    {
        // Unmapped means absent, not "fall back to a field called text". A store that guessed would
        // filter on a key this collection has no index for and drop every row.
        var channel = new CapturingSearchChannel([Point()]);

        await Store(channel, fields: Foreign with { Lexical = null }, analyzer: new IdentifierCodeAnalyzer())
            .SearchAsync("the screen says e33", TestContext.Current.CancellationToken);

        Assert.Empty(channel.LexicalKeys);
        Assert.Single(channel.Query!.Prefetch);
    }

    [Fact]
    public async Task Search_NoIdRoleMapped_FallsBackToThePointKey()
    {
        var key = Guid.NewGuid();
        var channel = new CapturingSearchChannel([Point(key)]);

        var card = Assert.Single(await Store(channel, fields: Foreign with { Id = null })
            .SearchAsync("anything", TestContext.Current.CancellationToken));

        Assert.Equal(key.ToString(), card.CardId);
    }

    [Fact]
    public async Task Search_NoSourceOrLocatorMapped_LeavesBothEmptyRatherThanGuessing()
    {
        var channel = new CapturingSearchChannel([Point()]);

        var card = Assert.Single(await Store(channel, fields: Foreign with { Source = null, Locator = null })
            .SearchAsync("anything", TestContext.Current.CancellationToken));

        Assert.Equal(string.Empty, card.SourceRef);
        Assert.Equal(string.Empty, card.SourceLocator);
    }

    [Fact]
    public async Task Search_ALinkFilter_ReadsTheLinkFieldAndMatchesOnTheMappedId()
    {
        var channel = new CapturingSearchChannel([Point()]);

        await Store(channel, links: new KnowledgeLinksConfiguration
        {
            Field = "related",
            Lookup = KnowledgeLinkLookup.Filter,
        }).SearchAsync("anything", TestContext.Current.CancellationToken);

        var condition = Assert.Single(channel.ScrollFilter!.Must);
        Assert.Equal("doc_id", condition.Field.Key);
        Assert.Equal(["DOC-99"], condition.Field.Match.Keywords.Strings);
    }

    [Fact]
    public async Task Search_AUuid5Link_HashesWithTheNamespaceAndPrefixTheDocumentNamed()
    {
        var channel = new CapturingSearchChannel([Point()]);

        // links.Namespace is a NAME; the adapter resolves it to a Guid and puts that on the options,
        // so a store built in code has to carry the resolved value. Setting only the name here leaves
        // the store on its own default and the derived key is wrong -- which is what this assertion
        // caught on the first run.
        await Store(
            channel,
            links: new KnowledgeLinksConfiguration
            {
                Field = "related",
                Lookup = KnowledgeLinkLookup.Uuid5,
                Namespace = "dns",
                Prefix = "doc:",
            },
            linkNamespace: Uuid5PointId.Namespace("dns"))
            .SearchAsync("anything", TestContext.Current.CancellationToken);

        Assert.Equal(
            [Uuid5PointId.For("DOC-99", Uuid5PointId.Namespace("dns"), "doc:")],
            Assert.Single(channel.RetrievedIds));
    }

    /// <summary>One scored point, shaped the way the foreign collection shapes one.</summary>
    private static ScoredPoint Point(Guid? key = null)
    {
        var point = new ScoredPoint
        {
            Id = new PointId { Uuid = (key ?? Guid.NewGuid()).ToString() },
            Score = 1f,
        };

        point.Payload["doc_id"] = "DOC-01";
        point.Payload["content"] = "the body text";
        point.Payload["origin"] = "handbook-7";
        point.Payload["page"] = "s.4";
        point.Payload["trust"] = 2;
        point.Payload["region"] = "emea";
        point.Payload["related"] = new Value
        {
            ListValue = new ListValue { Values = { new Value { StringValue = "DOC-99" } } },
        };

        return point;
    }

    private static KnowledgeScope Scope(string key, string value)
        => new() { Facets = new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value } };

    private static QdrantKnowledgeStore Store(
        CapturingSearchChannel channel,
        KnowledgeFieldsConfiguration? fields = null,
        string? template = null,
        KnowledgeLinksConfiguration? links = null,
        Guid? linkNamespace = null,
        IKnowledgeQueryAnalyzer? analyzer = null,
        bool scoped = false)
        => new(
            channel,
            new FakeEmbeddingGenerator([1f]),
            new QdrantKnowledgeStoreOptions
            {
                Collection = "anything",
                Scoped = scoped,
                Fields = fields ?? Foreign,
                ScopeTemplate = template,
                Links = links,
                LinkNamespace = linkNamespace ?? Uuid5PointId.Namespace(KnowledgeLinksConfiguration.DefaultNamespace),
                Analyzer = analyzer ?? new NoQueryAnalyzer(),
                Limit = 5,
                ScoreFloor = 0.0,
            });
}

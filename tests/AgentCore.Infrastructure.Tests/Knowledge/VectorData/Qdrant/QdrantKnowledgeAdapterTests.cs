using System.Diagnostics;
using System.Reflection;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Domain.Knowledge;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using AgentCore.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.AI;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

[Collection(QdrantServerCollection.Name)]
public sealed class QdrantKnowledgeAdapterTests : IClassFixture<KbShapedCorpusFixture>
{
    // Width 8, matching KbShapedCorpus.Dim: every collection this file builds by hand or through
    // KbShapedCorpus carries an 8-wide "dense" vector, so the dimension check must measure 8 too.
    private static readonly IEmbeddingGenerator<string, Embedding<float>> Embeddings =
        new FakeEmbeddingGenerator(new float[KbShapedCorpus.Dim]);

    private readonly KbShapedCorpusFixture _corpus;

    public QdrantKnowledgeAdapterTests(KbShapedCorpusFixture corpus) => _corpus = corpus;

    [QdrantFact]
    public async Task CreateSearchAsync_CollectionMissing_FailsTheStart()
    {
        // A29. AgentCore never creates. If it did, it would put an empty concrete collection where
        // the ingester's alias belongs, and the next ingest run then has a name it cannot claim.
        var adapter = new QdrantKnowledgeAdapter(_ => QdrantServer.CreateClient(), Embeddings);
        var entry = new KnowledgeProviderConfiguration
        {
            Kind = "qdrant",
            Collection = "does-not-exist",
            Vector = "dense",
            Fields = KbShapedCorpus.Fields,
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await adapter.CreateSearchAsync(entry, secrets: null, embeddings: null, requireScope: true, TestContext.Current.CancellationToken));

        Assert.Contains("does-not-exist", thrown.Message, StringComparison.Ordinal);

        // The message says what to do without naming anybody's ingest tool. AgentCore does not know
        // what wrote this collection, and a stranger told to "run kb sync" has nothing to run.
        Assert.Contains("never creates one", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("kb sync", thrown.Message, StringComparison.Ordinal);
        Assert.False(await ClientExists("does-not-exist"));
    }

    [QdrantFact]
    public async Task CreateSearchAsync_StartupFailure_DisposesTheClient()
    {
        // Every throw site inside the assertion body used to leave the client -- and the gRPC
        // channel behind it -- open. Reflection is used rather than asserting a specific downstream
        // exception, because Grpc.Net.Client does not guarantee what a disposed channel throws.
        QdrantClient? captured = null;
        var adapter = new QdrantKnowledgeAdapter(
            _ =>
            {
                captured = QdrantServer.CreateClient();
                return captured;
            },
            Embeddings);
        var entry = new KnowledgeProviderConfiguration
        {
            Kind = "qdrant",
            Collection = "does-not-exist",
            Vector = "dense",
            Fields = KbShapedCorpus.Fields,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await adapter.CreateSearchAsync(
                entry, secrets: null, embeddings: null, requireScope: true, TestContext.Current.CancellationToken));

        Assert.NotNull(captured);
        var isDisposed = typeof(QdrantClient)
            .GetField("_isDisposed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(captured);
        Assert.Equal(true, isDisposed);
    }

    [QdrantFact]
    public async Task CreateSearchAsync_DisposingTheReturnedPort_DisposesTheClient()
    {
        // The success-path counterpart to CreateSearchAsync_StartupFailure_DisposesTheClient: this is
        // what actually proves the QdrantKnowledgeStore -> QdrantSearchChannel -> QdrantClient
        // Dispose() forward reaches the real client, rather than a fake IDisposable standing in for
        // it. A host-level test on AgentCoreBoot's Track wiring cannot tell this apart from a Dispose()
        // that silently does nothing.
        var collection = $"dispose-{Guid.NewGuid():N}";
        using var setup = QdrantServer.CreateClient();
        await KbShapedCorpus.CreateAsync(setup, collection, interleaved: true, TestContext.Current.CancellationToken);

        QdrantClient? captured = null;
        var adapter = new QdrantKnowledgeAdapter(
            _ =>
            {
                captured = QdrantServer.CreateClient();
                return captured;
            },
            Embeddings);
        var entry = new KnowledgeProviderConfiguration
            {
                Kind = "qdrant",
                Collection = collection,
                Vector = "dense",
                Fields = KbShapedCorpus.Fields,
            };

        try
        {
            var port = await adapter.CreateSearchAsync(
                entry, secrets: null, embeddings: null, requireScope: true, TestContext.Current.CancellationToken);

            Assert.NotNull(captured);
            var isDisposedBeforeCall = typeof(QdrantClient)
                .GetField("_isDisposed", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(captured);
            Assert.Equal(false, isDisposedBeforeCall);

            ((IDisposable)port).Dispose();

            var isDisposedAfterCall = typeof(QdrantClient)
                .GetField("_isDisposed", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(captured);
            Assert.Equal(true, isDisposedAfterCall);
        }
        finally
        {
            await setup.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [QdrantFact]
    public async Task CreateSearchAsync_NamedVectorOverAnAnonymousCollection_FailsTheStart()
    {
        // A28, inverted by item 2 of the recommendation: the anonymous shape is now legal, so the
        // named check fires only when the document asked for a name this collection does not carry.
        var collection = $"unnamed-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await client.CreateCollectionAsync(
            collection, new VectorParams { Size = 8, Distance = Distance.Cosine },
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var adapter = new QdrantKnowledgeAdapter(_ => QdrantServer.CreateClient(), Embeddings);
            var entry = new KnowledgeProviderConfiguration
            {
                Kind = "qdrant",
                Collection = collection,
                Vector = "dense",
                Fields = KbShapedCorpus.Fields,
            };

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await adapter.CreateSearchAsync(
                    entry, secrets: null, embeddings: null, requireScope: true, TestContext.Current.CancellationToken));

            Assert.Contains("dense", thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [QdrantFact]
    public async Task CreateSearchAsync_DenseVectorIsTheWrongWidth_FailsTheStart()
    {
        // Of the three checks this class exists to make, this is the one an equal-widths test like
        // CreateSearchAsync_RealCollection_Opens cannot cover: a collection with a named "dense"
        // vector that is NOT 8-wide, so only the size comparison -- not the name check -- can catch it.
        var collection = $"wrongwidth-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await client.CreateCollectionAsync(
            collection,
            vectorsConfig: new VectorParamsMap
            {
                Map = { ["dense"] = new VectorParams { Size = 4, Distance = Distance.Cosine } },
            },
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var adapter = new QdrantKnowledgeAdapter(_ => QdrantServer.CreateClient(), Embeddings);
            var entry = new KnowledgeProviderConfiguration
            {
                Kind = "qdrant",
                Collection = collection,
                Vector = "dense",
                Fields = KbShapedCorpus.Fields,
            };

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await adapter.CreateSearchAsync(
                    entry, secrets: null, embeddings: null, requireScope: true, TestContext.Current.CancellationToken));

            // The surrounding phrase, not the bare digit: the message also carries a 32-character hex
            // collection name, so Contains("4") and Contains("8") both pass on a chance digit in it
            // even when the two widths have been swapped, or dropped from the message entirely.
            Assert.Contains("of 4 dimensions", thrown.Message, StringComparison.Ordinal);
            Assert.Contains($"embeds at {KbShapedCorpus.Dim}", thrown.Message, StringComparison.Ordinal);

            // Both ways out, named as document settings rather than as one repository's CLI.
            Assert.Contains("providers.embeddings", thrown.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("kb sync", thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [QdrantFact]
    public async Task CreateSearchAsync_RealCollection_Opens()
    {
        var collection = $"adapter-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await KbShapedCorpus.CreateAsync(client, collection, interleaved: true, TestContext.Current.CancellationToken);

        try
        {
            var adapter = new QdrantKnowledgeAdapter(_ => QdrantServer.CreateClient(), Embeddings);
            var entry = new KnowledgeProviderConfiguration
            {
                Kind = "qdrant",
                Collection = collection,
                Vector = "dense",
                Fields = KbShapedCorpus.Fields,
            };

            var port = await adapter.CreateSearchAsync(entry, secrets: null, embeddings: null, requireScope: true, TestContext.Current.CancellationToken);

            Assert.NotNull(port);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [QdrantFact]
    public async Task CreateSearchAsync_TheStoreItOpens_FetchesWhatTheMostGenerousAgentMayAskFor()
    {
        // Ruling 14(c). example.yaml gives the analyst limit: 8, the schema accepts up to 20, and the
        // store this adapter opened fetched 5 -- so the analyst asked for 8, received 5, and nothing
        // anywhere said so. One store serves every agent out of one fetch, so the fetch is the
        // document's ceiling and not the store's own convenience default.
        //
        // Asserted on the options the adapter handed the store rather than on a returned card count,
        // because the shipped ScoreFloor of 0.25 caps a plain query at three ranked cards whatever the
        // limit is -- see QdrantKnowledgeStoreTests.SearchAsync_ScoreFloor_KeepsExactlyTheCardsAtOrAboveIt,
        // which pins that boundary, and the fix report's concern about it. The behavioural half of
        // this chain is already pinned by SearchAsync_LimitAboveTheDefaultPrefetchDepth_ReturnsThatMany:
        // whatever Limit the store is given, it reaches Qdrant. This fact is the other half -- WHICH
        // limit the adapter gives it. The reflection idiom is the one this class already uses on
        // QdrantClient._isDisposed.
        var collection = $"limit-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await KbShapedCorpus.CreateAsync(client, collection, interleaved: true, TestContext.Current.CancellationToken);

        try
        {
            var adapter = new QdrantKnowledgeAdapter(_ => QdrantServer.CreateClient(), Embeddings);
            var entry = new KnowledgeProviderConfiguration
            {
                Kind = "qdrant",
                Collection = collection,
                Vector = "dense",
                Fields = KbShapedCorpus.Fields,
            };

            using var port = (IDisposable)await adapter.CreateSearchAsync(
                entry, secrets: null, embeddings: null, requireScope: false, TestContext.Current.CancellationToken);

            var options = (QdrantKnowledgeStoreOptions)typeof(QdrantKnowledgeStore)
                .GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(port)!;

            Assert.Equal(AgentKnowledgeConfiguration.MaximumLimit, options.Limit);
            Assert.True(
                options.Limit >= 8,
                $"example.yaml's analyst asks for 8 and this store fetches {options.Limit}");
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [QdrantFact]
    public async Task CreateSearchAsync_TheStoreItOpens_QueriesTheNamesTheStartupCheckAsserted()
    {
        // A guard that does not guard. The startup check reads a named vector off the collection and
        // refuses to boot without it, while the store used to query the options record's OWN "dense"
        // and "text" literals, which that check never governed. Both agreed, so nothing was broken;
        // change either one and the boot check still passes while every search misses every point,
        // silently, which is the exact failure the check was built to catch.
        // Asserted against the adapter's constants rather than against "dense" and "text", so this
        // fact follows a renamed constant instead of pinning a spelling. Asserted on the options the
        // adapter handed the store, the idiom this class already uses for Limit, because no corpus
        // can tell "queried the right name" from "queried a name that happens to match".
        var collection = $"names-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await KbShapedCorpus.CreateAsync(client, collection, interleaved: true, TestContext.Current.CancellationToken);

        try
        {
            var adapter = new QdrantKnowledgeAdapter(_ => QdrantServer.CreateClient(), Embeddings);
            var entry = new KnowledgeProviderConfiguration
            {
                Kind = "qdrant",
                Collection = collection,
                Vector = "dense",
                Fields = KbShapedCorpus.Fields,
            };

            using var port = (IDisposable)await adapter.CreateSearchAsync(
                entry, secrets: null, embeddings: null, requireScope: false, TestContext.Current.CancellationToken);

            var options = (QdrantKnowledgeStoreOptions)typeof(QdrantKnowledgeStore)
                .GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(port)!;

            Assert.Equal("dense", options.VectorName, StringComparer.Ordinal);
            Assert.Equal("text", options.Fields!.Lexical, StringComparer.Ordinal);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [QdrantFact]
    public async Task CreateSearchAsync_PayloadHasNoBody_FailsTheStart()
    {
        // The payload schema is owned by whatever ingests the cards, usually in another repository
        // on another release cycle, so it is the likeliest half
        // of this seam to drift -- and the only failure mode with no symptom: the store reads a missing
        // key as an empty string, "" satisfies every `required` on KnowledgeCard, and every turn of
        // every agent is then injected with blank cards. The fixture cannot catch it either; it writes
        // exactly the keys the store reads, so it can only ever agree with itself.
        var collection = $"nobody-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await KbShapedCorpus.CreateAsync(client, collection, interleaved: true, TestContext.Current.CancellationToken);

        try
        {
            // Exactly the drift being guarded against: the ingester renames body to text_body.
            Guid[] everyPoint =
                [.. Enumerable.Range(0, KbShapedCorpus.Count).Select(i => KbShapedCorpus.PointKey(KbShapedCorpus.Id(i)))];
            await client.DeletePayloadAsync(
                collection, ["body"], everyPoint, cancellationToken: TestContext.Current.CancellationToken);

            var adapter = new QdrantKnowledgeAdapter(_ => QdrantServer.CreateClient(), Embeddings);
            var entry = new KnowledgeProviderConfiguration
            {
                Kind = "qdrant",
                Collection = collection,
                Vector = "dense",
                Fields = KbShapedCorpus.Fields,
            };

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await adapter.CreateSearchAsync(
                    entry, secrets: null, embeddings: null, requireScope: false, TestContext.Current.CancellationToken));

            Assert.Contains(collection, thrown.Message, StringComparison.Ordinal);
            Assert.Contains("'body'", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("providers.knowledge.fields", thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [QdrantFact]
    public async Task CreateSearchAsync_EmptyCollection_Opens()
    {
        // The deliberate hole in the payload check, and the reason it is deliberate: a collection with
        // no points has no payload contract to violate, and refusing the start would make a host
        // unbootable in the window between creating the alias and the first ingest run filling it.
        var collection = $"empty-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await client.CreateCollectionAsync(
            collection,
            vectorsConfig: new VectorParamsMap
            {
                Map = { ["dense"] = new VectorParams { Size = KbShapedCorpus.Dim, Distance = Distance.Cosine } },
            },
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var adapter = new QdrantKnowledgeAdapter(_ => QdrantServer.CreateClient(), Embeddings);
            var entry = new KnowledgeProviderConfiguration
            {
                Kind = "qdrant",
                Collection = collection,
                Vector = "dense",
                Fields = KbShapedCorpus.Fields,
            };

            using var port = (IDisposable)await adapter.CreateSearchAsync(
                entry, secrets: null, embeddings: null, requireScope: false, TestContext.Current.CancellationToken);

            Assert.NotNull(port);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [QdrantFact]
    public async Task CreateSearchAsync_Uuid5LookupOverNumericPointKeys_FailsTheStart()
    {
        // The id check only runs when a point actually carries a links.field value, so this fixture
        // needs one such point -- with a numeric key, which no uuid5 or direct formula ever produces.
        var collection = $"numeric-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await client.CreateCollectionAsync(
            collection,
            vectorsConfig: new VectorParamsMap
            {
                Map = { ["dense"] = new VectorParams { Size = KbShapedCorpus.Dim, Distance = Distance.Cosine } },
            },
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var point = new PointStruct
            {
                Id = new PointId { Num = 1 },
                Vectors = new Vectors
                {
                    Vectors_ = new NamedVectors { Vectors = { ["dense"] = new float[KbShapedCorpus.Dim] } },
                },
            };
            point.Payload["card_id"] = "num-00";
            point.Payload["body"] = "numeric key card";
            point.Payload["see_also"] = new Value
            {
                ListValue = new ListValue { Values = { new Value { StringValue = "num-01" } } },
            };

            await client.UpsertAsync(
                collection, [point], cancellationToken: TestContext.Current.CancellationToken);

            var adapter = new QdrantKnowledgeAdapter(_ => QdrantServer.CreateClient(), Embeddings);
            var entry = new KnowledgeProviderConfiguration
            {
                Kind = "qdrant",
                Collection = collection,
                Vector = "dense",
                Fields = new KnowledgeFieldsConfiguration { Id = "card_id", Body = "body" },
                Links = KbShapedCorpus.Links,
            };

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await adapter.CreateSearchAsync(
                    entry, secrets: null, embeddings: null, requireScope: false, TestContext.Current.CancellationToken));

            Assert.Contains("keyed by number", failure.Message, StringComparison.Ordinal);
            Assert.Contains("links.lookup: filter", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [QdrantTheory]
    [InlineData(KnowledgeLinkLookup.Uuid5)]
    [InlineData(KnowledgeLinkLookup.Direct)]
    [InlineData(KnowledgeLinkLookup.Filter)]
    public async Task CreateSearchAsync_EmptyCollection_OpensUnderEveryLookupMode(KnowledgeLinkLookup lookup)
    {
        var collection = $"empty-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await client.CreateCollectionAsync(
            collection,
            vectorsConfig: new VectorParamsMap
            {
                Map = { ["dense"] = new VectorParams { Size = KbShapedCorpus.Dim, Distance = Distance.Cosine } },
            },
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var adapter = new QdrantKnowledgeAdapter(_ => QdrantServer.CreateClient(), Embeddings);
            var entry = new KnowledgeProviderConfiguration
            {
                Kind = "qdrant",
                Collection = collection,
                Vector = "dense",
                Fields = new KnowledgeFieldsConfiguration { Id = "card_id", Body = "body" },
                Links = KbShapedCorpus.Links with { Lookup = lookup },
            };

            using var port = (IDisposable)await adapter.CreateSearchAsync(
                entry, secrets: null, embeddings: null, requireScope: false, TestContext.Current.CancellationToken);

            Assert.NotNull(port);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void Kind_And_Capabilities_AreDeclared()
    {
        var adapter = new QdrantKnowledgeAdapter(_ => throw new UnreachableException(), Embeddings);

        Assert.Equal("qdrant", adapter.Kind);
        Assert.True(adapter.CanServeSearch);
        Assert.True(adapter.CanScope);
    }

    // The next two tests exercise the production constructor (no injected client factory), which
    // parses providers.knowledge.endpoint before ever touching the network. Neither needs a live
    // Qdrant: the failure happens before BuildClientAsync would open one.

    [Fact]
    public async Task CreateSearchAsync_NoGeneratorAnywhere_FailsAndNamesProvidersEmbeddings()
    {
        // Ruling 17a: the failure names the block to write. It must fire before any network work,
        // so the production constructor over an unreachable endpoint is safe here.
        var adapter = new QdrantKnowledgeAdapter();
        var entry = new KnowledgeProviderConfiguration
        {
            Kind = "qdrant",
            Collection = "unreachable",
            Endpoint = "https://cluster.example.com:6334",
            Fields = KbShapedCorpus.Fields,
        };

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await adapter.CreateSearchAsync(
                entry, secrets: null, embeddings: null, requireScope: false, TestContext.Current.CancellationToken));

        Assert.Equal("/providers/embeddings", failure.Pointer);
        Assert.Contains("providers.embeddings", failure.Message, StringComparison.Ordinal);
    }

    [QdrantFact]
    public async Task CreateSearchAsync_AGeneratorPassedThroughThePort_OpensTheStore()
    {
        // The providers.embeddings path: no generator in any constructor, the one the host built
        // arrives as the CreateSearchAsync argument.
        var collection = $"portgen-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await KbShapedCorpus.CreateAsync(client, collection, interleaved: true, TestContext.Current.CancellationToken);

        try
        {
            var adapter = new QdrantKnowledgeAdapter(_ => QdrantServer.CreateClient());
            var entry = new KnowledgeProviderConfiguration
            {
                Kind = "qdrant",
                Collection = collection,
                Vector = "dense",
                Fields = KbShapedCorpus.Fields,
            };

            using var port = (IDisposable)await adapter.CreateSearchAsync(
                entry, secrets: null, embeddings: Embeddings, requireScope: false, TestContext.Current.CancellationToken);

            Assert.NotNull(port);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CreateSearchAsync_NoEndpoint_FailsTheLoadAndPointsAtTheField()
    {
        var adapter = new QdrantKnowledgeAdapter(Embeddings);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await adapter.CreateSearchAsync(
                new KnowledgeProviderConfiguration
                {
                    Kind = "qdrant",
                    Collection = "unreachable",
                    Fields = KbShapedCorpus.Fields,
                },
                secrets: null, embeddings: null, requireScope: true, TestContext.Current.CancellationToken));

        Assert.Equal("/providers/knowledge/endpoint", failure.Pointer);
    }

    [Fact]
    public async Task CreateSearchAsync_EndpointNotAUrl_FailsTheLoadAndPointsAtTheSameField()
    {
        var adapter = new QdrantKnowledgeAdapter(Embeddings);
        var entry = new KnowledgeProviderConfiguration
        {
            Kind = "qdrant",
            Collection = "unreachable",
            Endpoint = "not a url",
            Fields = KbShapedCorpus.Fields,
        };

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await adapter.CreateSearchAsync(entry, secrets: null, embeddings: null, requireScope: true, TestContext.Current.CancellationToken));

        Assert.Equal("/providers/knowledge/endpoint", failure.Pointer);
    }

    [QdrantFact]
    public async Task CreateSearchAsync_WrongLinkPrefix_FailsTheStartNamingBothIds()
    {
        var entry = Entry() with
        {
            Links = KbShapedCorpus.Links with { Prefix = "wrong:" },
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Adapter().CreateSearchAsync(
                entry, secrets: null, Embedder(), requireScope: false,
                TestContext.Current.CancellationToken));

        Assert.Contains("links.lookup", failure.Message, StringComparison.Ordinal);
        Assert.Contains("prefix", failure.Message, StringComparison.Ordinal);
    }

    [QdrantFact]
    public async Task CreateSearchAsync_DirectLookupOverANonGuidCardId_NamesTheIdAndPointsAtFilter()
    {
        // links.lookup: direct reads neither namespace nor prefix, so the failure must not blame
        // them the way the uuid5 mismatch message does -- that would send an operator chasing a
        // setting this mode never looks at.
        //
        // A dedicated single-point collection, not the shared KbShapedCorpus: AssertPayloadAsync
        // scrolls one arbitrary point, and Qdrant does not guarantee which one comes back. Every
        // KbShapedCorpus card is non-GUID, so the assertion would still fire no matter which point
        // the scroll returned -- but the id it names would not be predictable, and naming the right
        // id is the whole point of this test.
        const string cardId = "direct-01";
        var collection = $"direct-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await client.CreateCollectionAsync(
            collection,
            vectorsConfig: new VectorParamsMap
            {
                Map = { ["dense"] = new VectorParams { Size = KbShapedCorpus.Dim, Distance = Distance.Cosine } },
            },
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var point = new PointStruct
            {
                Id = new PointId { Uuid = Guid.NewGuid().ToString() },
                Vectors = new Vectors
                {
                    Vectors_ = new NamedVectors { Vectors = { ["dense"] = new float[KbShapedCorpus.Dim] } },
                },
            };
            point.Payload["card_id"] = cardId;
            point.Payload["body"] = "direct lookup card";
            point.Payload["see_also"] = new Value { ListValue = new ListValue() };

            await client.UpsertAsync(collection, [point], cancellationToken: TestContext.Current.CancellationToken);

            var adapter = new QdrantKnowledgeAdapter(_ => QdrantServer.CreateClient(), Embeddings);
            var entry = new KnowledgeProviderConfiguration
            {
                Kind = "qdrant",
                Collection = collection,
                Vector = "dense",
                Fields = new KnowledgeFieldsConfiguration { Id = "card_id", Body = "body" },
                Links = KbShapedCorpus.Links with { Lookup = KnowledgeLinkLookup.Direct },
            };

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await adapter.CreateSearchAsync(
                    entry, secrets: null, embeddings: null, requireScope: false,
                    TestContext.Current.CancellationToken));

            Assert.Contains(cardId, failure.Message, StringComparison.Ordinal);
            Assert.Contains("not a GUID", failure.Message, StringComparison.Ordinal);
            Assert.Contains("links.lookup: filter", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("namespace", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("prefix", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CreateSearchAsync_LinksWithoutAnIdField_FailsTheLoadPointingAtFieldsId()
    {
        // Item 1 of the recommendation: a feature error is raised against the document, before any
        // network work, so this test needs no server.
        var adapter = new QdrantKnowledgeAdapter(Embeddings);
        var entry = new KnowledgeProviderConfiguration
        {
            Kind = "qdrant",
            Collection = "unreachable",
            Endpoint = "https://cluster.example.com:6334",
            Fields = new KnowledgeFieldsConfiguration { Body = "body", Id = null },
            Links = new KnowledgeLinksConfiguration { Field = "see_also" },
        };

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await adapter.CreateSearchAsync(
                entry, secrets: null, embeddings: null, requireScope: false, TestContext.Current.CancellationToken));

        Assert.Equal("/providers/knowledge/fields/id", failure.Pointer);
        Assert.Contains("links", failure.Message, StringComparison.Ordinal);
    }

    [QdrantFact]
    public async Task CreateSearchAsync_FilterLookupIgnoresNamespaceAndPrefix_Starts()
    {
        // filter derives no key, so uuid5's couplings must not be able to fail it.
        var entry = Entry() with
        {
            Links = KbShapedCorpus.Links with { Lookup = KnowledgeLinkLookup.Filter, Prefix = "wrong:", Namespace = "dns" },
        };

        var store = await Adapter().CreateSearchAsync(
            entry, secrets: null, Embedder(), requireScope: false,
            TestContext.Current.CancellationToken);

        Assert.NotNull(store);
        (store as IDisposable)?.Dispose();
    }

    [QdrantFact]
    public async Task CreateSearchAsync_NoLinksBlock_SkipsTheRoundTripCheckEntirely()
    {
        // KbShapedCorpus points DO carry see_also and uuid5 keys, but with no links: block there is
        // no feature to protect and no check to run.
        using var store = await Adapter().CreateSearchAsync(
            Entry(), secrets: null, Embedder(), requireScope: false,
            TestContext.Current.CancellationToken) as IDisposable ?? throw new InvalidOperationException();

        Assert.NotNull(store);
    }

    [QdrantFact]
    public async Task CreateSearchAsync_NoLinksFieldInPayload_SkipsTheIdCheck()
    {
        // The prefix is wrong AND the links field names nothing in the payload. No expansion can
        // ever run, so there is nothing for the id check to protect and the start must succeed.
        var entry = Entry() with
        {
            Links = KbShapedCorpus.Links with { Field = "no_such_field", Prefix = "wrong:" },
        };

        var store = await Adapter().CreateSearchAsync(
            entry, secrets: null, Embedder(), requireScope: false,
            TestContext.Current.CancellationToken);

        Assert.NotNull(store);
        (store as IDisposable)?.Dispose();
    }

    [QdrantFact]
    public async Task CreateSearchAsync_RenamedVector_FailsNamingTheConfiguredName()
    {
        var entry = Entry() with { Vector = "not_dense" };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Adapter().CreateSearchAsync(
                entry, secrets: null, Embedder(), requireScope: false,
                TestContext.Current.CancellationToken));

        Assert.Contains("not_dense", failure.Message, StringComparison.Ordinal);
    }

    [QdrantFact]
    public async Task CreateSearchAsync_RenamedBodyFieldThatIsAbsent_FailsNamingIt()
    {
        var entry = Entry() with
        {
            Fields = new KnowledgeFieldsConfiguration { Body = "no_such_body" },
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Adapter().CreateSearchAsync(
                entry, secrets: null, Embedder(), requireScope: false,
                TestContext.Current.CancellationToken));

        Assert.Contains("no_such_body", failure.Message, StringComparison.Ordinal);
    }

    [QdrantFact]
    public async Task CreateSearchAsync_UnknownAnalyzer_FailsNamingWhatIsRegistered()
    {
        var entry = Entry() with { Analyzer = "clause-numbers" };

        var failure = await Assert.ThrowsAnyAsync<Exception>(
            async () => await Adapter().CreateSearchAsync(
                entry, secrets: null, Embedder(), requireScope: false,
                TestContext.Current.CancellationToken));

        Assert.Contains("clause-numbers", failure.Message, StringComparison.Ordinal);
        Assert.Contains("identifier-codes", failure.Message, StringComparison.Ordinal);
        Assert.Contains("none", failure.Message, StringComparison.Ordinal);
    }

    [QdrantFact]
    public async Task CreateSearchAsync_CustomAnalyzer_IsSelectedByName()
    {
        var entry = Entry() with { Analyzer = "always-empty" };

        var store = await Adapter()
            .UseAnalyzers(new AlwaysEmptyAnalyzer())
            .CreateSearchAsync(
                entry, secrets: null, Embedder(), requireScope: false,
                TestContext.Current.CancellationToken);

        Assert.NotNull(store);
        (store as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CreateSearchAsync_UnknownMapper_FailsTheLoadPointingAtMapper()
    {
        var adapter = new QdrantKnowledgeAdapter(Embeddings);
        var entry = new KnowledgeProviderConfiguration
        {
            Kind = "qdrant",
            Collection = "unreachable",
            Endpoint = "https://cluster.example.com:6334",
            Mapper = "acme-catalog",
        };

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await adapter.CreateSearchAsync(
                entry, secrets: null, embeddings: null, requireScope: false, TestContext.Current.CancellationToken));

        Assert.Equal("/providers/knowledge/mapper", failure.Pointer);
        Assert.Contains("acme-catalog", failure.Message, StringComparison.Ordinal);
        Assert.Contains("UseMappers", failure.Message, StringComparison.Ordinal);
    }

    [QdrantFact]
    public async Task CreateSearchAsync_CustomMapper_OpensAndMapsEverySearchThroughIt()
    {
        var entry = Entry() with { Mapper = "upper-body" };

        var port = await Adapter()
            .UseMappers(new UpperBodyMapper())
            .CreateSearchAsync(
                entry, secrets: null, Embedder(), requireScope: false,
                TestContext.Current.CancellationToken);

        var cards = await port.SearchAsync("deck", TestContext.Current.CancellationToken);

        Assert.NotEmpty(cards);
        Assert.All(cards, card => Assert.Equal(card.Text.ToUpperInvariant(), card.Text));
        (port as IDisposable)?.Dispose();
    }

    [QdrantFact]
    public async Task CreateSearchAsync_CustomMapperThatReadsBlank_FailsTheStartNamingTheMapper()
    {
        var entry = Entry() with { Mapper = "blank" };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Adapter()
                .UseMappers(new BlankMapper())
                .CreateSearchAsync(
                    entry, secrets: null, Embedder(), requireScope: false,
                    TestContext.Current.CancellationToken));

        Assert.Contains("'blank'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("blank cards", failure.Message, StringComparison.Ordinal);
    }

    [QdrantFact]
    public async Task CreateSearchAsync_IdRoleDisabled_OpensACollectionWithNoIdField()
    {
        // Item 1 of the recommendation: body is the only required role. This collection carries
        // nothing but body, and the document says so with fields.id: null.
        var collection = $"noid-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await client.CreateCollectionAsync(
            collection,
            vectorsConfig: new VectorParamsMap
            {
                Map = { ["dense"] = new VectorParams { Size = KbShapedCorpus.Dim, Distance = Distance.Cosine } },
            },
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var point = new PointStruct
            {
                Id = new PointId { Uuid = Guid.NewGuid().ToString() },
                Vectors = new Vectors
                {
                    Vectors_ = new NamedVectors { Vectors = { ["dense"] = new float[KbShapedCorpus.Dim] } },
                },
            };
            point.Payload["body"] = "a card with no id concept";
            await client.UpsertAsync(collection, [point], cancellationToken: TestContext.Current.CancellationToken);

            var adapter = new QdrantKnowledgeAdapter(_ => QdrantServer.CreateClient(), Embeddings);
            var entry = new KnowledgeProviderConfiguration
            {
                Kind = "qdrant",
                Collection = collection,
                Vector = "dense",

                // This collection holds one point carrying a body and nothing else, so the entry
                // maps a body and nothing else.
                Fields = new KnowledgeFieldsConfiguration { Body = "body" },
            };

            using var port = (IDisposable)await adapter.CreateSearchAsync(
                entry, secrets: null, embeddings: null, requireScope: false, TestContext.Current.CancellationToken);

            Assert.NotNull(port);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [QdrantFact]
    public async Task CreateSearchAsync_DefaultIdOverACollectionWithoutOne_StillFailsTheStart()
    {
        // The other half of the convention: defaults are declarations. A document that never
        // mentions fields.id declared card_id, and a collection without it is still drift.
        var collection = $"noiddefault-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await client.CreateCollectionAsync(
            collection,
            vectorsConfig: new VectorParamsMap
            {
                Map = { ["dense"] = new VectorParams { Size = KbShapedCorpus.Dim, Distance = Distance.Cosine } },
            },
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var point = new PointStruct
            {
                Id = new PointId { Uuid = Guid.NewGuid().ToString() },
                Vectors = new Vectors
                {
                    Vectors_ = new NamedVectors { Vectors = { ["dense"] = new float[KbShapedCorpus.Dim] } },
                },
            };
            point.Payload["body"] = "a card with no id concept";
            await client.UpsertAsync(collection, [point], cancellationToken: TestContext.Current.CancellationToken);

            var adapter = new QdrantKnowledgeAdapter(_ => QdrantServer.CreateClient(), Embeddings);
            var entry = new KnowledgeProviderConfiguration
            {
                Kind = "qdrant",
                Collection = collection,
                Vector = "dense",
                Fields = KbShapedCorpus.Fields,
            };

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await adapter.CreateSearchAsync(
                    entry, secrets: null, embeddings: null, requireScope: false, TestContext.Current.CancellationToken));

            Assert.Contains("'card_id'", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [QdrantFact]
    public async Task CreateSearchAsync_NoVectorNamed_OpensAnAnonymousVectorCollection()
    {
        // Item 2 of the recommendation, the single biggest compatibility win: the default shape the
        // Qdrant client, MEVD's connector, LangChain, LlamaIndex and Spring AI all create.
        var collection = $"anonadapter-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await client.CreateCollectionAsync(
            collection,
            new VectorParams { Size = KbShapedCorpus.Dim, Distance = Distance.Cosine },
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var point = new PointStruct
            {
                Id = new PointId { Uuid = Guid.NewGuid().ToString() },
                Vectors = new float[KbShapedCorpus.Dim],
            };
            point.Payload["card_id"] = "anon-00";
            point.Payload["body"] = "anonymous vector card";
            await client.UpsertAsync(collection, [point], cancellationToken: TestContext.Current.CancellationToken);

            var adapter = new QdrantKnowledgeAdapter(_ => QdrantServer.CreateClient(), Embeddings);
            var entry = new KnowledgeProviderConfiguration
            {
                Kind = "qdrant",
                Collection = collection,
                Fields = new KnowledgeFieldsConfiguration { Id = "card_id", Body = "body" },
            };

            using var port = (IDisposable)await adapter.CreateSearchAsync(
                entry, secrets: null, embeddings: null, requireScope: false, TestContext.Current.CancellationToken);

            Assert.NotNull(port);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [QdrantFact]
    public async Task CreateSearchAsync_NoVectorNamedOverNamedVectors_FailsTheStart()
    {
        var entry = Entry() with { Vector = null };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Adapter().CreateSearchAsync(
                entry, secrets: null, Embedder(), requireScope: false,
                TestContext.Current.CancellationToken));

        Assert.Contains("providers.knowledge.vector", failure.Message, StringComparison.Ordinal);
        Assert.Contains("names none", failure.Message, StringComparison.Ordinal);
    }

    private sealed class AlwaysEmptyAnalyzer : IKnowledgeQueryAnalyzer
    {
        public string Name => "always-empty";

        public IReadOnlyList<string> RequiredTerms(string query) => [];
    }

    private sealed class UpperBodyMapper : IKnowledgePointMapper
    {
        public string Name => "upper-body";

        public KnowledgeCard? Map(KnowledgePoint point) => new()
        {
            CardId = point.PointId,
            Text = (point.Payload.TryGetValue("body", out var body) ? body as string ?? string.Empty : string.Empty)
                .ToUpperInvariant(),
            ViaLink = false,
        };
    }

    private sealed class BlankMapper : IKnowledgePointMapper
    {
        public string Name => "blank";

        public KnowledgeCard? Map(KnowledgePoint point) => new()
        {
            CardId = point.PointId,
            Text = string.Empty,
            ViaLink = false,
        };
    }

    private KnowledgeProviderConfiguration Entry() => new()
    {
        Kind = QdrantKnowledgeAdapter.ProviderKind,
        Collection = _corpus.Name,
        Vector = "dense",

        // The corpus describes its own payload. Nothing here is inherited: AgentCore ships no
        // field names, so an entry that omits this block reads nothing off any point.
        Fields = KbShapedCorpus.Fields,
        Scope = KbShapedCorpus.Scope,
    };

    private static QdrantKnowledgeAdapter Adapter() => new(_ => QdrantServer.CreateClient());

    private static FakeEmbeddingGenerator Embedder() => new(KbShapedCorpus.QueryVector());

    private static async Task<bool> ClientExists(string collection)
    {
        using var client = QdrantServer.CreateClient();
        return await client.CollectionExistsAsync(collection, TestContext.Current.CancellationToken);
    }
}

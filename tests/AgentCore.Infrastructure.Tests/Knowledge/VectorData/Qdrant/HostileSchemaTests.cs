using AgentCore.Application.Configuration.Schema;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using AgentCore.Infrastructure.Tests.Fakes;
using Qdrant.Client;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>
/// A collection that does not carry what the document claims must fail the START.
/// </summary>
/// <remarks>
/// The alternative is not an exception later — it is no exception at all. Every unmapped or
/// mismapped role reads as absent, so the store opens, every search answers, and every card
/// reaches the model with an empty body or a blank citation. Nothing downstream can tell that
/// apart from a knowledge base that simply had no match.
/// </remarks>
[Collection(QdrantServerCollection.Name)]
public sealed class HostileSchemaTests
{
    [QdrantFact]
    public async Task NumericPointKeys_UnderUuid5Links_FailTheStartAndPointAtFilter()
    {
        // Only the roles this collection really carries. The payload proof runs BEFORE the
        // point-key proof, so a document that also mismapped a citation role would fail on that
        // first and never reach the branch under test here.
        await Against(
            HostileCorpus.NumericKeysAsync,
            Entry() with
            {
                Fields = Fields with { Source = null, Locator = null, Authority = null },
                Links = new KnowledgeLinksConfiguration
                {
                    Field = "related",
                    Lookup = KnowledgeLinkLookup.Uuid5,
                },
            },
            message =>
            {
                Assert.Contains("keyed by number", message, StringComparison.Ordinal);
                Assert.Contains("links.lookup: filter", message, StringComparison.Ordinal);
            });
    }

    [QdrantFact]
    public async Task ABodyWrittenAsChunks_FailsTheStartNamingTheRole()
    {
        // The store reads one string. A list is not a shorter string: it reads as absent, and every
        // card would carry an empty body.
        await Against(
            HostileCorpus.ChunkedBodyAsync,
            Entry(),
            message =>
            {
                Assert.Contains("'content'", message, StringComparison.Ordinal);
                Assert.Contains("fields.body", message, StringComparison.Ordinal);
            });
    }

    [QdrantFact]
    public async Task CitationRolesTheCollectionDoesNotCarry_FailTheStart()
    {
        // The exact silent failure the widened startup proof exists for: source and locator are
        // mapped, the collection carries neither, and before the proof every citation was blank
        // for the life of the deployment with nothing logged.
        await Against(
            HostileCorpus.NoCitationFieldsAsync,
            Entry(),
            message =>
            {
                Assert.Contains("'origin'", message, StringComparison.Ordinal);
                Assert.Contains("fields.source", message, StringComparison.Ordinal);
                Assert.Contains("explicit null", message, StringComparison.Ordinal);
            });
    }

    [QdrantFact]
    public async Task ATextAuthority_FailsTheStartRatherThanReadingAsNoRank()
    {
        await Against(
            HostileCorpus.TextAuthorityAsync,
            Entry() with { Fields = Fields with { Source = null, Locator = null } },
            message =>
            {
                Assert.Contains("numeric", message, StringComparison.Ordinal);
                Assert.Contains("fields.authority", message, StringComparison.Ordinal);
            });
    }

    [QdrantFact]
    public async Task DeclaringTheMissingRolesAsNull_OpensTheSameCollection()
    {
        // The other half of every failure above: the document says what the collection really has,
        // and the same collection opens. A refusal a deployment cannot answer is just a bug.
        var collection = $"hostile-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await HostileCorpus.NoCitationFieldsAsync(client, collection, TestContext.Current.CancellationToken);

        try
        {
            var entry = Entry() with
            {
                Collection = collection,
                Fields = Fields with { Source = null, Locator = null, Authority = null },
            };

            using var port = (IDisposable)await Adapter().CreateSearchAsync(
                entry, secrets: null, Embedder(), requireScope: false, TestContext.Current.CancellationToken);

            Assert.NotNull(port);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Builds one hostile collection, starts against it, and reads the refusal.</summary>
    private static async Task Against(
        Func<QdrantClient, string, CancellationToken, Task> build,
        KnowledgeProviderConfiguration entry,
        Action<string> assertMessage)
    {
        var collection = $"hostile-{Guid.NewGuid():N}";
        using var client = QdrantServer.CreateClient();
        await build(client, collection, TestContext.Current.CancellationToken);

        try
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await Adapter().CreateSearchAsync(
                    entry with { Collection = collection },
                    secrets: null,
                    Embedder(),
                    requireScope: false,
                    TestContext.Current.CancellationToken));

            assertMessage(thrown.Message);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private static KnowledgeFieldsConfiguration Fields => new()
    {
        Id = "doc_id",
        Body = "content",
        Lexical = "content",
        Source = "origin",
        Locator = "page",
        Authority = "trust",
    };

    private static KnowledgeProviderConfiguration Entry() => new()
    {
        Kind = QdrantKnowledgeAdapter.ProviderKind,
        Collection = "replaced-per-test",
        Fields = Fields,
    };

    private static QdrantKnowledgeAdapter Adapter() => new(_ => QdrantServer.CreateClient());

    private static FakeEmbeddingGenerator Embedder() => new(HostileCorpus.QueryVector());
}

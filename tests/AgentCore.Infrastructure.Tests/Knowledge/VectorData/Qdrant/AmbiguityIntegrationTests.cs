using System.Reflection;
using System.Runtime.CompilerServices;
using AgentCore.Application.Calls;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.State;
using AgentCore.Domain.Knowledge;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using AgentCore.Infrastructure.Tests.Fakes;
using AgentCore.TestSupport;
using Grpc.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>
/// Section 12's "Vocabulary and probe -- against real Qdrant" list, and the Task A7 debt: a live
/// <see cref="CallSession"/> turn proving the boot-filled <see cref="VocabularyCache"/> reaches the
/// extractor's gate, not just a hand-seeded one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="KnowledgeProviderFactory"/>, <see cref="Clarifications"/> and <see cref="ClarificationText"/>
/// are internal to <c>AgentCore.Application</c>, and this project carries no grant to reach them. Every
/// row here is instead driven through the same seam a production host uses: a real YAML document
/// compiled by <see cref="ConfigurationCompiler"/> with a real <see cref="QdrantKnowledgeStore"/> handed
/// in as <see cref="AgentCompilationContext.Knowledge"/>, and a real <see cref="CallSession"/> turn. The
/// turn's own "reply" model is a fake that, instead of letting the framework's tool-calling loop run,
/// reaches directly into the compiled agent's own <c>TextSearchProvider</c> and calls its private search
/// delegate itself -- the same delegate <c>KnowledgeProviderFactory.Create</c> built from the real store,
/// reached by reflection into a <em>third-party</em> field exactly as
/// <c>AgentCore.Application.Tests.Knowledge.KnowledgeProbeTests</c> already does, which needs no grant
/// because reflection does not go through the C# compiler's accessibility check. Calling it from inside
/// the fake model, rather than after the turn, is what keeps <c>TurnAmbients.Current</c> populated: that
/// ambient is only entered around <c>CallSession</c>'s own call into the reply model.
/// </para>
/// <para>
/// The store fuses a plain dense leg with a required-term leg by RRF (K49's amendment): a card the
/// required-term leg does not match can still surface through the dense leg alone, and
/// <c>QdrantKnowledgeStoreTests.SearchAsync_LookalikeIdentifier_RanksTheIdentifierCardFirst</c> already
/// pins that a matching identifier only lifts a card to the top rank, never excludes a non-matching
/// one. A required-term token therefore cannot isolate a single card from the shared corpus by itself;
/// the row that needs exactly one card (the two-machine card) combines a distinctive token with a
/// store <c>Limit</c> of 1, so only the fused winner survives. Every row that needs a card population
/// this shared fixture must not carry for every other row -- the company-wide card, the one-machine
/// multi-card union, and the empty-search row -- builds and drops its own small collection instead,
/// the same way <see cref="FacetVocabularyTests"/>'s wildcard fact does.
/// </para>
/// </remarks>
public sealed class AmbiguityCorpusFixture : IAsyncLifetime
{
    /// <summary>The id of the added card whose <c>facets.model</c> names two machines.</summary>
    public const string MultiMachineCardId = "syn-multi";

    /// <summary>The required-term token that reaches only <see cref="MultiMachineCardId"/>.</summary>
    public const string MultiMachineToken = "e77";

    /// <summary>A required-term token no card in this corpus carries.</summary>
    public const string NothingToken = "e99";

    /// <summary>The two machines <see cref="MultiMachineCardId"/> names, in the order the note must join them.</summary>
    public static readonly string[] MultiMachineModels = ["ct900", "ctsbs900"];

    /// <summary>The value every card here carries at <c>facets.audience</c>, so a second scope facet always resolves.</summary>
    public const string Audience = "everyone";

    public string Name { get; } = $"ambiguity-probe-{Guid.NewGuid():N}";

    public QdrantClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!QdrantServer.IsConfigured)
        {
            return;
        }

        Client = QdrantServer.CreateClient();

        // The 30-card synthetic corpus, unmodified, plus the multi-machine card the brief calls for.
        // Composing with KbShapedCorpus rather than inventing a second one: A4's own facet-read tests
        // already proved this corpus's payload shape works, and a wrong facet path is this design's
        // own central failure mode (section 12), so reusing the shape that is already proven is the
        // point. The "*" card lives in its own throwaway collection instead (see
        // CompanyWideQuestion_ExactlyOneMatchingCard_Answers_ProbeDoesNotRun): giving every card here
        // model: "*" would make it the only card any scope: { model: "*" } search could ever find,
        // which is exactly the row this shared fixture must not answer for every other one.
        await KbShapedCorpus.CreateAsync(Client, Name, interleaved: true, TestContext.Current.CancellationToken);

        await Client.CreatePayloadIndexAsync(
            Name, "facets.audience", PayloadSchemaType.Keyword, cancellationToken: TestContext.Current.CancellationToken);

        var added = new[]
        {
            CardWithModelList(MultiMachineCardId, $"err {MultiMachineToken} shared drive belt code on the console", MultiMachineModels),
        };
        await Client.UpsertAsync(Name, added, cancellationToken: TestContext.Current.CancellationToken);

        // One call sets facets.audience on every point already in the collection -- the 30 base cards
        // and the two just added -- rather than 32 individual patches. The all-points overload sends
        // no points_selector at all, which this server version refuses ("points_selector is
        // expected"); an empty filter is what Qdrant treats as "every point", and this client always
        // wraps a Filter argument in a PointsSelector before it reaches the wire.
        await Client.SetPayloadAsync(
            Name,
            payload: new Dictionary<string, Value> { ["audience"] = new Value { StringValue = Audience } },
            filter: new Filter(),
            key: "facets",
            cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Client is null)
        {
            return;
        }

        try
        {
            await Client.DeleteCollectionAsync(Name);
        }
        finally
        {
            Client.Dispose();
        }
    }

    internal static PointStruct CardWithModelScalar(string cardId, string text, string model)
        => Card(cardId, text, new Value { StringValue = model });

    internal static PointStruct CardWithModelList(string cardId, string text, IReadOnlyList<string> models)
        => Card(cardId, text, new Value
        {
            ListValue = new ListValue { Values = { models.Select(model => new Value { StringValue = model }) } },
        });

    internal static PointStruct Card(string cardId, string text, Value modelValue)
    {
        var point = new PointStruct
        {
            Id = new PointId { Uuid = Guid.NewGuid().ToString() },
            Vectors = new Vectors
            {
                Vectors_ = new NamedVectors { Vectors = { ["dense"] = KbShapedCorpus.QueryVector() } },
            },
        };

        point.Payload["card_id"] = cardId;
        point.Payload["text"] = text;
        point.Payload["body"] = text;
        point.Payload["authority"] = 3;
        point.Payload["see_also"] = new Value { ListValue = new ListValue() };
        point.Payload["facets"] = new Value
        {
            StructValue = new Struct
            {
                Fields =
                {
                    ["model"] = modelValue,
                    ["audience"] = new Value { StringValue = Audience },
                },
            },
        };
        point.Payload["source"] = new Value
        {
            StructValue = new Struct
            {
                Fields =
                {
                    ["ref"] = new Value { StringValue = $"manifest-{cardId}" },
                    ["locator"] = new Value { StringValue = "p.1" },
                },
            },
        };

        return point;
    }
}

[Collection(QdrantServerCollection.Name)]
public sealed class AmbiguityIntegrationTests : IClassFixture<AmbiguityCorpusFixture>
{
    private const string ModelDescription = "The model, as printed on the machine.";

    private const string NoticeSourceName = "agentcore:notice";

    /// <summary>Two droppable-shaped facets: <c>model</c> (the one the probe drops) and the always-concrete <c>audience</c>, so K33 never blocks the drop.</summary>
    private const string TwoFacetYaml =
        """
        apiVersion: agentcore/v1
        name: ambiguity-probe-integration
        state:
          model:
            type: string
            writer: extractor
            description: "The model, as printed on the machine."
            vocabulary: { from: knowledge }
          audience:
            type: string
            writer: const
            value: everyone
            enum: [everyone]
        extractor:
          model: { ref: fill }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          knowledge:
            kind: qdrant
            collection: kb
            fields: { body: text }
            scope:
              template: "facets.{key}"
              fromState: [model, audience]
              wildcard: { value: "*", facets: [model] }
            ambiguity: { maxCandidates: {{maxCandidates}}, maxAsks: 2 }
        agents:
          items:
            - id: only
              knowledge: { mode: tool, scoped: true }
        """;

    /// <summary>One droppable-shaped facet only: dropping it would open the scope empty (K33).</summary>
    private const string SingleFacetYaml =
        """
        apiVersion: agentcore/v1
        name: ambiguity-probe-single-facet
        state:
          model:
            type: string
            writer: extractor
            vocabulary: { from: knowledge }
        extractor:
          model: { ref: fill }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          knowledge:
            kind: qdrant
            collection: kb
            fields: { body: text }
            scope:
              template: "facets.{key}"
              fromState: [model]
              wildcard: { value: "*", facets: [model] }
            ambiguity: {}
        agents:
          items:
            - id: only
              knowledge: { mode: tool, scoped: true }
        """;

    /// <summary>No <c>scope:</c> at all: the agent opens the whole corpus regardless of what the caller has said.</summary>
    private const string UnscopedYaml =
        """
        apiVersion: agentcore/v1
        name: ambiguity-probe-unscoped
        agents:
          items:
            - { id: only, instructions: "answer the caller", knowledge: { mode: tool, scoped: false } }
        """;

    /// <summary>No ambiguity, no wildcard: a plain <c>vocabulary:</c> slot the extractor writes and the gate checks.</summary>
    private const string VocabularyOnlyYaml =
        """
        apiVersion: agentcore/v1
        name: vocabulary-cache-reaches-extractor
        state:
          model:
            type: string
            writer: extractor
            description: "The model, as printed on the machine."
            vocabulary: { from: knowledge }
        extractor:
          model: { ref: fill }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          knowledge:
            kind: qdrant
            collection: kb
            fields: { body: text }
            scope:
              template: "facets.{key}"
        agents:
          items:
            - id: only
              instructions: "answer the caller"
        """;

    private readonly AmbiguityCorpusFixture _corpus;

    public AmbiguityIntegrationTests(AmbiguityCorpusFixture corpus) => _corpus = corpus;

    // -----------------------------------------------------------------------------------------
    // Section 12's twelve real-Qdrant rows.
    // -----------------------------------------------------------------------------------------

    /// <summary>A company-wide question with exactly one matching card answers, and the probe does not run.</summary>
    /// <remarks>
    /// This card's own <c>facets.model</c> is the wildcard value, so a <c>scope: { model: "*" }</c>
    /// search finds it under any query. It cannot live in the shared 31-card fixture: it would then be
    /// the one card every other row's "unknown model" scope can always find too, since that scope
    /// filter only ever admits a card whose own facet is literally "*". A throwaway collection, built
    /// and dropped here, is what keeps this row's fact from leaking into every other one.
    /// </remarks>
    [QdrantFact]
    public async Task CompanyWideQuestion_ExactlyOneMatchingCard_Answers_ProbeDoesNotRun()
    {
        var collection = $"ambiguity-adhoc-{Guid.NewGuid():N}";

        try
        {
            var store = await FillAdHocCollectionAsync(
                collection,
                [AmbiguityCorpusFixture.CardWithModelScalar("syn-wild", "general policy text for every machine", "*")],
                scoped: true);

            var client = await RunAsync(BuildTwoFacetYaml(), "what is the policy for every machine", store);

            // A genuine card reached the model -- the probe's own gate (cards.Count == 0) never
            // opened, because a notice is the only shape a probe or a no-scope refusal ever returns.
            Assert.Contains(client.Results, r => r.SourceName != NoticeSourceName);
            Assert.Contains(client.Results, r => r.Text.Contains("general policy", StringComparison.Ordinal));
        }
        finally
        {
            await _corpus.Client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A per-machine question produces the note, naming all three ids.</summary>
    [QdrantFact]
    public async Task PerMachineQuestion_ProducesTheNote_NamingAllThreeIds()
    {
        var client = await RunAsync(BuildTwoFacetYaml(), KbShapedCorpus.PlainQuery);

        var text = Assert.Single(client.Results).Text;
        Assert.Contains(ModelDescription, text, StringComparison.Ordinal);
        Assert.Contains("ct900ent", text, StringComparison.Ordinal);
        Assert.Contains("ctsbs900", text, StringComparison.Ordinal);
        Assert.Contains("ct900,", text, StringComparison.Ordinal);
    }

    /// <summary>A question whose cards all belong to one machine produces the one-candidate confirm text, not "holds nothing".</summary>
    /// <remarks>
    /// §12's row names "cards", plural: several cards must actually reach the probe's union and
    /// collapse to one value, not a single card standing in for the whole row (that shape is already
    /// <c>KnowledgeProbeTests.K24_OneCardIsNeverASpread</c>'s job, at the unit level). Four cards, all
    /// carrying <c>facets.model: ct900</c>, live in their own throwaway collection so the union is
    /// exactly {ct900} by real collapsing over multiple cards, not because there was nothing else to
    /// find.
    /// </remarks>
    [QdrantFact]
    public async Task QuestionWhoseCardsAllBelongToOneMachine_ProducesTheOneCandidateConfirmText()
    {
        var collection = $"ambiguity-adhoc-{Guid.NewGuid():N}";

        try
        {
            var cards = Enumerable.Range(0, 4)
                .Select(i => AmbiguityCorpusFixture.CardWithModelScalar(
                    $"syn-single-{i}", $"card {i} deck belt maintenance text", "ct900"))
                .ToArray();
            var store = await FillAdHocCollectionAsync(collection, cards, scoped: true);

            var client = await RunAsync(BuildTwoFacetYaml(), KbShapedCorpus.PlainQuery, store);

            var text = Assert.Single(client.Results).Text;
            Assert.DoesNotContain("holds nothing", text, StringComparison.Ordinal);
            Assert.Contains("decides the answer here", text, StringComparison.Ordinal);
            Assert.Contains("Everything found is for ct900.", text, StringComparison.Ordinal);
        }
        finally
        {
            await _corpus.Client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A card tagged with two machines produces a note naming both.</summary>
    [QdrantFact]
    public async Task CardTaggedWithTwoMachines_ProducesANoteNamingBoth()
    {
        var client = await RunAsync(
            BuildTwoFacetYaml(), $"the screen says {AmbiguityCorpusFixture.MultiMachineToken}", limit: 1);

        var text = Assert.Single(client.Results).Text;
        foreach (var model in AmbiguityCorpusFixture.MultiMachineModels)
        {
            Assert.Contains(model, text, StringComparison.Ordinal);
        }
    }

    /// <summary>More than <c>maxCandidates</c> names none of them.</summary>
    [QdrantFact]
    public async Task MoreThanMaxCandidates_NamesNone()
    {
        var client = await RunAsync(BuildTwoFacetYaml(maxCandidates: 2), KbShapedCorpus.PlainQuery);

        var text = Assert.Single(client.Results).Text;
        Assert.DoesNotContain("holds nothing", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ct900", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ctsbs900", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A probe spread publishes no sources. <c>TurnSources</c> is internal to <c>CallSession</c> and
    /// unreachable without a grant this project does not have, so this reads the same fact at its
    /// outer edge instead: nothing card-shaped reached the model at all, only the notice -- which is
    /// exactly what "published no sources" requires, since only a card-shaped result is ever cited.
    /// </summary>
    [QdrantFact]
    public async Task AProbeSpread_PublishesNoSources()
    {
        var client = await RunAsync(BuildTwoFacetYaml(maxCandidates: 2), KbShapedCorpus.PlainQuery);

        // Assert.All over an empty sequence passes vacuously; this pins that the probe actually spoke
        // before checking what it carried.
        Assert.Single(client.Results);
        Assert.All(client.Results, r => Assert.Equal(NoticeSourceName, r.SourceName));
    }

    /// <summary>No notice can be cited as a card: every notice this design emits carries the reserved source name.</summary>
    [QdrantFact]
    public async Task NoNoticeCanBeCitedAsACard()
    {
        var holdsNothing = await RunAsync(SingleFacetYaml, $"the screen says {AmbiguityCorpusFixture.NothingToken}");
        Assert.Single(holdsNothing.Results);
        Assert.All(holdsNothing.Results, r => Assert.Equal(NoticeSourceName, r.SourceName));

        var named = await RunAsync(
            BuildTwoFacetYaml(), $"the screen says {AmbiguityCorpusFixture.MultiMachineToken}", limit: 1);
        Assert.Single(named.Results);
        Assert.All(named.Results, r => Assert.Equal(NoticeSourceName, r.SourceName));
    }

    /// <summary>A facet read against a collection with <c>maxValues</c> values fails startup.</summary>
    [QdrantFact]
    public async Task FacetRead_AgainstACollectionWithMaxValuesValues_FailsStartup()
    {
        // This is the exact call sequence KnowledgeStartup.ApplyVocabularyAsync runs for one slot
        // (port.ReadAsync then VocabularyCache.Replace with the same limit) -- KnowledgeStartup itself
        // is internal to AgentCore.AspNetCore, a project this one does not reference.
        var port = BuildStore();
        var values = await port.ReadAsync("facets.model", limit: 3, TestContext.Current.CancellationToken);

        Assert.Equal(3, values.Count);

        var vocabulary = new VocabularyCache();
        var failure = Assert.Throws<VocabularyException>(() => vocabulary.Replace("model", values, maxValues: 3));
        Assert.Equal("model", failure.Slot);
    }

    /// <summary>A facet read against a path with no keyword index fails startup.</summary>
    [QdrantFact]
    public async Task FacetRead_AgainstAPathWithNoKeywordIndex_FailsStartup()
    {
        var port = BuildStore();

        // "body" carries no payload index at all in this corpus -- the same uncaught exception
        // ApplyVocabularyAsync would let propagate straight out of a boot it never wraps in a try.
        var failure = await Assert.ThrowsAsync<RpcException>(
            async () => await port.ReadAsync("body", 100, TestContext.Current.CancellationToken));

        Assert.Equal(StatusCode.InvalidArgument, failure.StatusCode);
        // §10 requires the exception to name the offending path -- the only part of this row a real
        // Qdrant response actually decides, as opposed to the fact of throwing at all.
        Assert.Contains("body", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A single-facet <c>fromState</c> deployment does not throw: the probe is skipped and the turn returns "holds nothing" (K33).</summary>
    [QdrantFact]
    public async Task SingleFacetFromStateDeployment_DoesNotThrow_ProbeIsSkipped_HoldsNothing()
    {
        var client = await RunAsync(SingleFacetYaml, $"the screen says {AmbiguityCorpusFixture.NothingToken}");

        Assert.Contains("holds nothing", Assert.Single(client.Results).Text, StringComparison.Ordinal);
    }

    /// <summary>An unscoped agent's empty search returns an empty list (§8 step 2).</summary>
    /// <remarks>
    /// An unscoped agent opens <c>WholeCorpus</c> -- no facets at all -- so the store's own filter is
    /// empty and admits every point. Against the shared fixture that would always find something (the
    /// closest card by dense rank, regardless of the query's own text, since a fixed test embedding
    /// answers every query alike); a genuinely empty collection is what makes the search answer
    /// nothing, and is the one true test of "empty", independent of ranking.
    /// </remarks>
    [QdrantFact]
    public async Task UnscopedAgent_EmptySearch_ReturnsAnEmptyList()
    {
        var collection = $"ambiguity-adhoc-{Guid.NewGuid():N}";

        try
        {
            var store = await FillAdHocCollectionAsync(collection, [], scoped: false);

            var client = await RunAsync(UnscopedYaml, "anything at all", store);

            Assert.Empty(client.Results);
        }
        finally
        {
            await _corpus.Client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// A probe that throws returns "holds nothing", logs its own event, and leaves the main search's
    /// audit record intact (K32).
    /// </summary>
    /// <remarks>
    /// The real store answers the main search; only the probe's own narrowed second search is a
    /// synthetic throw, because the two searches share every scope facet but the one being dropped, so
    /// there is no real Qdrant misconfiguration that fails one and not the other (verified by reading
    /// <c>QdrantKnowledgeStore</c>'s filter construction before choosing this shape).
    /// </remarks>
    [QdrantFact]
    public async Task ProbeThatThrows_ReturnsHoldsNothing_LogsItsOwnEvent_LeavesTheMainSearchAuditRecordIntact()
    {
        RecordingLoggerFactory loggers = new();
        var port = new ThrowingOnNarrowedScopePort(BuildStore(), fullFacetCount: 2);

        var client = await RunAsync(
            BuildTwoFacetYaml(), $"the screen says {AmbiguityCorpusFixture.MultiMachineToken}", port, loggers);

        Assert.Contains("holds nothing", Assert.Single(client.Results).Text, StringComparison.Ordinal);

        // EventId 17: KnowledgeProbeFailed -- the probe's own failure event.
        var probeFailed = Assert.Single(loggers.Of(17));
        Assert.Equal("model", probeFailed.Field<string>("Facet"));

        // EventId 11: KnowledgeRetrieved -- the main search's own audit record, recorded before the
        // probe ever ran, and untouched by the probe's later failure.
        Assert.Single(loggers.Of(11));

        // EventId 12: KnowledgeRetrievalFailed -- the main search itself never failed, so this never fires.
        Assert.Empty(loggers.Of(12));
    }

    // -----------------------------------------------------------------------------------------
    // The Task A7 debt: a live turn proving the boot-filled vocabulary cache reaches the extractor.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A value read live from the real collection's own facet -- never hand-seeded -- reaches the
    /// extractor's gate and links. Task A7 proved the cache reaches <c>CallSessionFactory</c> only by
    /// code review, a construction smoke test and a shared-instance proof in the refresh test; this is
    /// the first place a value that could only have come from that boot read is actually linked.
    /// </summary>
    [QdrantFact]
    public async Task LiveTurn_BootFilledVocabularyCache_LinksAReallyReadValue()
    {
        var vocabulary = await SeedVocabularyFromRealQdrantAsync();

        RoutingChatClientFactory chatClients = new(new FixedTextChatClient("okay."));
        chatClients.Route("fill", new FixedTextChatClient("""{"model":"ct900ent"}"""));

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(VocabularyOnlyYaml),
            new AgentCompilationContext(chatClients) { Knowledge = BuildStore() });

        var extractor = CallSessionFactory.CreateExtractor(compiled, chatClients);
        var session = new CallSessionFactory(
            compiled, new GuardEvaluator(compiled.Configuration.Guards), extractor, vocabulary: vocabulary)
            .Create("call-a7-live-extractor-positive");

        await session.RunTurnAsync("I have a CT900ENT", TestContext.Current.CancellationToken);

        // "ct900ent" is not a value this test wrote anywhere -- it is the corpus's own spelling, read
        // back from Qdrant into `vocabulary` a few lines above and nowhere else in this method.
        Assert.Equal("ct900ent", session.State.Read("model")?.GetValue<string>());
    }

    /// <summary>
    /// The gate's other half: a value the same real read never produced is refused, proving the accept
    /// above is really checking the boot-filled cache and not simply accepting any string.
    /// </summary>
    [QdrantFact]
    public async Task LiveTurn_AValueTheBootReadNeverProduced_IsRefused()
    {
        var vocabulary = await SeedVocabularyFromRealQdrantAsync();

        RoutingChatClientFactory chatClients = new(new FixedTextChatClient("okay."));
        chatClients.Route("fill", new FixedTextChatClient("""{"model":"xt385"}"""));

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(VocabularyOnlyYaml),
            new AgentCompilationContext(chatClients) { Knowledge = BuildStore() });

        var extractor = CallSessionFactory.CreateExtractor(compiled, chatClients);
        var session = new CallSessionFactory(
            compiled, new GuardEvaluator(compiled.Configuration.Guards), extractor, vocabulary: vocabulary)
            .Create("call-a7-live-extractor-negative");

        await session.RunTurnAsync("I have an XT385", TestContext.Current.CancellationToken);

        Assert.Null(session.State.Read("model"));
    }

    // -----------------------------------------------------------------------------------------
    // Helpers.
    // -----------------------------------------------------------------------------------------

    private async Task<VocabularyCache> SeedVocabularyFromRealQdrantAsync()
    {
        var port = BuildStore();
        var values = await port.ReadAsync("facets.model", limit: 2000, TestContext.Current.CancellationToken);

        var vocabulary = new VocabularyCache();
        vocabulary.Replace("model", values, maxValues: 2000, wildcardValue: "*");
        return vocabulary;
    }

    private async Task<SearchCapturingChatClient> RunAsync(
        string yaml,
        string callerQuestion,
        IKnowledgeRetrievalPort? port = null,
        RecordingLoggerFactory? loggers = null,
        int limit = 10)
    {
        SearchCapturingChatClient capture = new(callerQuestion);
        RoutingChatClientFactory chatClients = new(capture);
        chatClients.Route("fill", new FixedTextChatClient("{}"));

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(yaml),
            new AgentCompilationContext(chatClients)
            {
                Knowledge = port ?? BuildStore(limit),
                Loggers = loggers,
            });

        capture.Bind(SearchProviderOf(compiled.Agents["only"]));

        var extractor = CallSessionFactory.CreateExtractor(compiled, chatClients);
        var session = new CallSessionFactory(
            compiled, new GuardEvaluator(compiled.Configuration.Guards), extractor, vocabulary: new VocabularyCache())
            .Create($"call-{Guid.NewGuid():N}");

        await session.RunTurnAsync(callerQuestion, TestContext.Current.CancellationToken);

        return capture;
    }

    private static string BuildTwoFacetYaml(int maxCandidates = 6)
        => TwoFacetYaml.Replace("{{maxCandidates}}", maxCandidates.ToString(), StringComparison.Ordinal);

    private QdrantKnowledgeStore BuildStore(int limit = 10) => BuildStoreOver(_corpus.Name, limit, scoped: true);

    private QdrantKnowledgeStore BuildStoreOver(string collection, int limit, bool scoped) => new(
        new QdrantSearchChannel(_corpus.Client),
        new FakeEmbeddingGenerator(KbShapedCorpus.QueryVector()),
        new QdrantKnowledgeStoreOptions
        {
            Collection = collection,
            Scoped = scoped,
            VectorName = "dense",
            Fields = KbShapedCorpus.Fields,
            ScopeTemplate = KbShapedCorpus.ScopeTemplate,
            ScopeWildcard = "*",
            ScopeWildcardFacets = ["model"],
            Analyzer = KbShapedCorpus.Analyzer,
            Limit = limit,
            ScoreFloor = 0.0,
        });

    /// <summary>
    /// Creates and fills one throwaway collection, already named by the caller, for one test. Used by
    /// the rows whose own card population must not be visible to any other row's search of the shared
    /// fixture (see <see cref="AmbiguityCorpusFixture"/>'s remarks).
    /// </summary>
    /// <remarks>
    /// The caller generates <paramref name="collection"/> and opens its own <c>try</c> **before**
    /// calling this -- never the reverse. Collection creation, every index, and the upsert can each
    /// throw against a live, sometimes-contended server, and if that happened inside an un-tried
    /// helper the collection this call already created on the server would be orphaned with nothing
    /// left to drop it. Matches <see cref="FacetVocabularyTests.ReadAsync_WildcardValueComesBackAsAnOrdinaryValue"/>'s
    /// own shape: creation, indexing and upsert all inside one <c>try</c>, one unconditional
    /// <c>DeleteCollectionAsync</c> in the caller's own <c>finally</c>.
    /// </remarks>
    private async Task<QdrantKnowledgeStore> FillAdHocCollectionAsync(
        string collection, PointStruct[] points, bool scoped)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await _corpus.Client.CreateCollectionAsync(
            collection,
            vectorsConfig: new VectorParamsMap
            {
                Map = { ["dense"] = new VectorParams { Size = KbShapedCorpus.Dim, Distance = Distance.Cosine } },
            },
            cancellationToken: cancellationToken);

        await _corpus.Client.CreatePayloadIndexAsync(
            collection, "text", PayloadSchemaType.Text, cancellationToken: cancellationToken);
        await _corpus.Client.CreatePayloadIndexAsync(
            collection, "facets.model", PayloadSchemaType.Keyword, cancellationToken: cancellationToken);
        await _corpus.Client.CreatePayloadIndexAsync(
            collection, "facets.audience", PayloadSchemaType.Keyword, cancellationToken: cancellationToken);

        if (points.Length > 0)
        {
            await _corpus.Client.UpsertAsync(collection, points, cancellationToken: cancellationToken);
        }

        return BuildStoreOver(collection, limit: 10, scoped);
    }

    private static TextSearchProvider SearchProviderOf(AIAgent agent)
    {
        var inner = agent.GetService<ChatClientAgent>()
            ?? throw new InvalidOperationException("the compiled agent carries no ChatClientAgent.");

        return inner.AIContextProviders?.OfType<TextSearchProvider>().SingleOrDefault()
            ?? throw new InvalidOperationException("the compiled agent carries no TextSearchProvider.");
    }

    /// <summary>
    /// A knowledge store that answers the full-scope (main) search from a real backing store and
    /// throws for any narrowed one -- the shape §8 step 4's own probe search takes once a facet is
    /// dropped. Used only for the row that needs the probe's own second search to fail.
    /// </summary>
    private sealed class ThrowingOnNarrowedScopePort : IKnowledgeRetrievalPort
    {
        private readonly IKnowledgeRetrievalPort _inner;
        private readonly int _fullFacetCount;

        internal ThrowingOnNarrowedScopePort(IKnowledgeRetrievalPort inner, int fullFacetCount)
        {
            _inner = inner;
            _fullFacetCount = fullFacetCount;
        }

        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, CancellationToken cancellationToken = default)
        {
            if (KnowledgeScopeScope.Current?.Facets.Count == _fullFacetCount)
            {
                return _inner.SearchAsync(query, cancellationToken);
            }

            throw new InvalidOperationException("the probe's own second search is down (synthetic, for this row only).");
        }
    }

    /// <summary>
    /// Stands in for the turn's reply model. Rather than let the framework's own tool-calling loop
    /// run, it reaches directly into the compiled agent's <c>TextSearchProvider</c> and calls its
    /// private search delegate itself, from inside the same ambient scope <see cref="CallSession"/>
    /// already opened around this call -- so <c>TurnAmbients.Current</c> is exactly what a real tool
    /// invocation would see.
    /// </summary>
    private sealed class SearchCapturingChatClient : IChatClient
    {
        private readonly string _query;

        private Func<string, CancellationToken, Task<IEnumerable<TextSearchProvider.TextSearchResult>>>? _search;

        internal SearchCapturingChatClient(string query) => _query = query;

        internal IReadOnlyList<TextSearchProvider.TextSearchResult> Results { get; private set; } = [];

        internal void Bind(TextSearchProvider provider)
            => _search = TextSearchProviderInternals.SearchDelegate(provider);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            if (_search is { } search)
            {
                Results = [.. await search(_query, cancellationToken).ConfigureAwait(false)];
            }

            await Task.Yield();

            var responseId = Guid.NewGuid().ToString("N");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "noted.")
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>
    /// A model that always answers with the same fixed text, ignoring whatever transcript it is
    /// handed. Stands in for both the turn's reply model and its extractor: the reply's own words do
    /// not matter to any row here, and the extractor's fixed text is read as JSON.
    /// </summary>
    private sealed class FixedTextChatClient : IChatClient
    {
        private readonly string _text;

        internal FixedTextChatClient(string text) => _text = text;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await Task.Yield();

            var responseId = Guid.NewGuid().ToString("N");
            yield return new ChatResponseUpdate(ChatRole.Assistant, _text)
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}

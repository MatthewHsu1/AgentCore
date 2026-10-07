using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.State;
using AgentCore.Domain.Knowledge;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using AgentCore.Infrastructure.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant
{
    /// <summary>
    /// The ambiguity probe against a real Qdrant server.
    /// </summary>
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

            // The 30-card synthetic corpus, unmodified, plus the multi-machine card the brief conversations for.
            // Composing with KbShapedCorpus rather than inventing a second one: the facet-read tests
            // already proved this corpus's payload shape works, and a wrong facet path is this design's
            // own central failure mode, so reusing the shape that is already proven is the
            // point. The "*" card lives in its own throwaway collection instead (see
            // CompanyWideQuestion_ExactlyOneMatchingCard_Answers_ProbeDoesNotRun): giving every card here
            // model: "*" would make it the only card any scope: { model: "*" } search could ever find,
            // which is exactly the row this shared fixture must not answer for every other one.
            await KbShapedCorpus.CreateAsync(Client, Name, interleaved: true, TestContext.Current.CancellationToken);

            _ = await Client.CreatePayloadIndexAsync(
                Name, "facets.audience", PayloadSchemaType.Keyword, cancellationToken: TestContext.Current.CancellationToken);

            PointStruct[] added =
            [
                CardWithModelList(MultiMachineCardId, $"err {MultiMachineToken} shared drive belt code on the console", MultiMachineModels),
            ];
            _ = await Client.UpsertAsync(Name, added, cancellationToken: TestContext.Current.CancellationToken);

            // One conversation sets facets.audience on every point already in the collection -- the 30 base cards
            // and the two just added -- rather than 32 individual patches. The all-points overload sends
            // no points_selector at all, which this server version refuses ("points_selector is
            // expected"); an empty filter is what Qdrant treats as "every point", and this client always
            // wraps a Filter argument in a PointsSelector before it reaches the wire.
            _ = await Client.SetPayloadAsync(
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
        {
            return Card(cardId, text, new Value { StringValue = model });
        }

        internal static PointStruct CardWithModelList(string cardId, string text, IReadOnlyList<string> models)
        {
            return Card(cardId, text, new Value
            {
                ListValue = new ListValue { Values = { models.Select(model => new Value { StringValue = model }) } },
            });
        }

        internal static PointStruct Card(string cardId, string text, Value modelValue)
        {
            PointStruct point = new()
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
    public sealed class AmbiguityIntegrationTests(AmbiguityCorpusFixture corpus) : IClassFixture<AmbiguityCorpusFixture>
    {
        private const string ModelDescription = "The model, as printed on the machine.";

        private const string NoticeSourceName = "agentcore:notice";

        /// <summary>Two droppable-shaped facets: <c>model</c> (the one the probe drops) and the always-concrete <c>audience</c>, so the probe is never skipped for having a single facet.</summary>
        private const string TwoFacetYaml =
            """
        apiVersion: agentcore/v1
        state:
          model:
            type: string
            writer: extractor
            description: "The model, as printed on the machine."
            enum: [ct900, ctsbs900, ct900ent]
          audience:
            type: string
            writer: const
            value: everyone
            enum: [everyone]
        extractor:
          model: { ref: fill }
        providers:
          conversation:   { kind: telnyx-relay }
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
        entries:
          main:
            agent: only
        """;

        /// <summary>One droppable-shaped facet only: dropping it would open the scope empty.</summary>
        private const string SingleFacetYaml =
            """
        apiVersion: agentcore/v1
        state:
          model:
            type: string
            writer: extractor
            enum: [ct900, ctsbs900, ct900ent]
        extractor:
          model: { ref: fill }
        providers:
          conversation:   { kind: telnyx-relay }
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
        entries:
          main:
            agent: only
        """;

        /// <summary>No <c>scope:</c> at all: the agent opens the whole corpus regardless of what the caller has said.</summary>
        private const string UnscopedYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "answer the caller", knowledge: { mode: tool, scoped: false } }
        entries:
          main:
            agent: only
        """;

        private readonly AmbiguityCorpusFixture _corpus = corpus;


        /// <summary>A company-wide question with exactly one matching card answers, and the probe does not run.</summary>
        [QdrantFact]
        public async Task CompanyWideQuestion_ExactlyOneMatchingCard_Answers_ProbeDoesNotRun()
        {
            string collection = $"ambiguity-adhoc-{Guid.NewGuid():N}";

            try
            {
                QdrantKnowledgeStore store = await FillAdHocCollectionAsync(
                    collection,
                    [AmbiguityCorpusFixture.CardWithModelScalar("syn-wild", "general policy text for every machine", "*")],
                    scoped: true);

                SearchCapturingChatClient client = await RunAsync(BuildTwoFacetYaml(), "what is the policy for every machine", store);

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
            SearchCapturingChatClient client = await RunAsync(BuildTwoFacetYaml(), KbShapedCorpus.PlainQuery);

            string text = Assert.Single(client.Results).Text;
            Assert.Contains(ModelDescription, text, StringComparison.Ordinal);
            Assert.Contains("ct900ent", text, StringComparison.Ordinal);
            Assert.Contains("ctsbs900", text, StringComparison.Ordinal);
            Assert.Contains("ct900,", text, StringComparison.Ordinal);
        }

        /// <summary>A question whose cards all belong to one machine produces the one-candidate confirm text, not "holds nothing".</summary>
        [QdrantFact]
        public async Task QuestionWhoseCardsAllBelongToOneMachine_ProducesTheOneCandidateConfirmText()
        {
            string collection = $"ambiguity-adhoc-{Guid.NewGuid():N}";

            try
            {
                PointStruct[] cards = [.. Enumerable.Range(0, 4)
                    .Select(i => AmbiguityCorpusFixture.CardWithModelScalar(
                        $"syn-single-{i}", $"card {i} deck belt maintenance text", "ct900"))];
                QdrantKnowledgeStore store = await FillAdHocCollectionAsync(collection, cards, scoped: true);

                SearchCapturingChatClient client = await RunAsync(BuildTwoFacetYaml(), KbShapedCorpus.PlainQuery, store);

                string text = Assert.Single(client.Results).Text;
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
            SearchCapturingChatClient client = await RunAsync(
                BuildTwoFacetYaml(), $"the screen says {AmbiguityCorpusFixture.MultiMachineToken}", limit: 1);

            string text = Assert.Single(client.Results).Text;
            foreach (string model in AmbiguityCorpusFixture.MultiMachineModels)
            {
                Assert.Contains(model, text, StringComparison.Ordinal);
            }
        }

        /// <summary>More than <c>maxCandidates</c> names none of them.</summary>
        [QdrantFact]
        public async Task MoreThanMaxCandidates_NamesNone()
        {
            SearchCapturingChatClient client = await RunAsync(BuildTwoFacetYaml(maxCandidates: 2), KbShapedCorpus.PlainQuery);

            string text = Assert.Single(client.Results).Text;
            Assert.DoesNotContain("holds nothing", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ct900", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ctsbs900", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// A probe spread publishes no sources. <c>TurnSources</c> is internal to <c>ConversationSession</c> and
        /// unreachable without a grant this project does not have, so this reads the same fact at its
        /// outer edge instead: nothing card-shaped reached the model at all, only the notice -- which is
        /// exactly what "published no sources" requires, since only a card-shaped result is ever cited.
        /// </summary>
        [QdrantFact]
        public async Task AProbeSpread_PublishesNoSources()
        {
            SearchCapturingChatClient client = await RunAsync(BuildTwoFacetYaml(maxCandidates: 2), KbShapedCorpus.PlainQuery);

            // Assert.All over an empty sequence passes vacuously; this pins that the probe actually spoke
            // before checking what it carried.
            _ = Assert.Single(client.Results);
            Assert.All(client.Results, r => Assert.Equal(NoticeSourceName, r.SourceName));
        }

        /// <summary>No notice can be cited as a card: every notice this design emits carries the reserved source name.</summary>
        [QdrantFact]
        public async Task NoNoticeCanBeCitedAsACard()
        {
            SearchCapturingChatClient holdsNothing = await RunAsync(SingleFacetYaml, $"the screen says {AmbiguityCorpusFixture.NothingToken}");
            _ = Assert.Single(holdsNothing.Results);
            Assert.All(holdsNothing.Results, r => Assert.Equal(NoticeSourceName, r.SourceName));

            SearchCapturingChatClient named = await RunAsync(
                BuildTwoFacetYaml(), $"the screen says {AmbiguityCorpusFixture.MultiMachineToken}", limit: 1);
            _ = Assert.Single(named.Results);
            Assert.All(named.Results, r => Assert.Equal(NoticeSourceName, r.SourceName));
        }

        /// <summary>A single-facet <c>fromState</c> deployment does not throw: the probe is skipped and the turn returns "holds nothing".</summary>
        [QdrantFact]
        public async Task SingleFacetFromStateDeployment_DoesNotThrow_ProbeIsSkipped_HoldsNothing()
        {
            SearchCapturingChatClient client = await RunAsync(SingleFacetYaml, $"the screen says {AmbiguityCorpusFixture.NothingToken}");

            Assert.Contains("holds nothing", Assert.Single(client.Results).Text, StringComparison.Ordinal);
        }

        /// <summary>An unscoped agent's empty search returns an empty list.</summary>
        [QdrantFact]
        public async Task UnscopedAgent_EmptySearch_ReturnsAnEmptyList()
        {
            string collection = $"ambiguity-adhoc-{Guid.NewGuid():N}";

            try
            {
                QdrantKnowledgeStore store = await FillAdHocCollectionAsync(collection, [], scoped: false);

                SearchCapturingChatClient client = await RunAsync(UnscopedYaml, "anything at all", store);

                Assert.Empty(client.Results);
            }
            finally
            {
                await _corpus.Client.DeleteCollectionAsync(collection, cancellationToken: TestContext.Current.CancellationToken);
            }
        }

        /// <summary>
        /// A probe that throws returns "holds nothing", logs its own event, and leaves the main search's
        /// audit record intact.
        /// </summary>
        [QdrantFact]
        public async Task ProbeThatThrows_ReturnsHoldsNothing_LogsItsOwnEvent_LeavesTheMainSearchAuditRecordIntact()
        {
            RecordingLoggerFactory loggers = new();
            ThrowingOnNarrowedScopePort port = new(BuildStore(), fullFacetCount: 2);

            SearchCapturingChatClient client = await RunAsync(
                BuildTwoFacetYaml(), $"the screen says {AmbiguityCorpusFixture.MultiMachineToken}", port, loggers);

            Assert.Contains("holds nothing", Assert.Single(client.Results).Text, StringComparison.Ordinal);

            // EventId 17: KnowledgeProbeFailed -- the probe's own failure event.
            CapturedLine probeFailed = Assert.Single(loggers.Of(17));
            Assert.Equal("model", probeFailed.Field<string>("Facet"));

            // EventId 11: KnowledgeRetrieved -- the main search's own audit record, recorded before the
            // probe ever ran, and untouched by the probe's later failure.
            _ = Assert.Single(loggers.Of(11));

            // EventId 12: KnowledgeRetrievalFailed -- the main search itself never failed, so this never fires.
            Assert.Empty(loggers.Of(12));
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
            _ = chatClients.Route("fill", new FixedTextChatClient("{}"));

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(yaml),
                new AgentCompilationContext(chatClients)
                {
                    Knowledge = port ?? BuildStore(limit),
                    Loggers = loggers,
                })["main"];

            StateExtractor? extractor = ConversationSessionFactory.CreateExtractor(compiled, chatClients);
            ConversationSession session = new ConversationSessionFactory(
                compiled, new GuardEvaluator(compiled.Configuration.Guards), extractor)
                .Create($"conversation-{Guid.NewGuid():N}");

            _ = await session.RunTurnAsync(callerQuestion, TestContext.Current.CancellationToken);

            return capture;
        }

        private static string BuildTwoFacetYaml(int maxCandidates = 6)
        {
            return TwoFacetYaml.Replace("{{maxCandidates}}", maxCandidates.ToString(), StringComparison.Ordinal);
        }

        private QdrantKnowledgeStore BuildStore(int limit = 10)
        {
            return BuildStoreOver(_corpus.Name, limit, scoped: true);
        }

        private QdrantKnowledgeStore BuildStoreOver(string collection, int limit, bool scoped)
        {
            return new(
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
        }

        /// <summary>
        /// Creates and fills one throwaway collection, already named by the caller, for one test. Used by
        /// the rows whose own card population must not be visible to any other row's search of the shared
        /// fixture.
        /// </summary>
        private async Task<QdrantKnowledgeStore> FillAdHocCollectionAsync(
            string collection, PointStruct[] points, bool scoped)
        {
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;

            await _corpus.Client.CreateCollectionAsync(
                collection,
                vectorsConfig: new VectorParamsMap
                {
                    Map = { ["dense"] = new VectorParams { Size = KbShapedCorpus.Dim, Distance = Distance.Cosine } },
                },
                cancellationToken: cancellationToken);

            _ = await _corpus.Client.CreatePayloadIndexAsync(
                collection, "text", PayloadSchemaType.Text, cancellationToken: cancellationToken);
            _ = await _corpus.Client.CreatePayloadIndexAsync(
                collection, "facets.model", PayloadSchemaType.Keyword, cancellationToken: cancellationToken);
            _ = await _corpus.Client.CreatePayloadIndexAsync(
                collection, "facets.audience", PayloadSchemaType.Keyword, cancellationToken: cancellationToken);

            if (points.Length > 0)
            {
                _ = await _corpus.Client.UpsertAsync(collection, points, cancellationToken: cancellationToken);
            }

            return BuildStoreOver(collection, limit: 10, scoped);
        }

        /// <summary>
        /// A knowledge store that answers the full-scope (main) search from a real backing store and
        /// throws for any narrowed one -- the shape the probe's own search takes once a facet is
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
                string query, KnowledgeScope? scope = null, CancellationToken cancellationToken = default)
            {
                return scope?.Facets.Count == _fullFacetCount
                    ? _inner.SearchAsync(query, scope, cancellationToken)
                    : throw new InvalidOperationException("the probe's own second search is down (synthetic, for this row only).");
            }
        }

        /// <summary>
        /// Stands in for the turn's reply model. Rather than answer, its first round calls the compiled
        /// agent's own <c>Search</c> tool with the row's question, the way a model that needs the cards
        /// would; the framework's own tool-calling loop then invokes the real search with the running
        /// turn filed in its arguments, from inside the same flow scope <see cref="ConversationSession"/> opened
        /// around this conversation. Its second round reads the tool's own answer back off the transcript and
        /// keeps it, so every row asserts on what the production search actually returned.
        /// </summary>
        private sealed class SearchCapturingChatClient : IChatClient
        {
            private readonly string _query;

            internal SearchCapturingChatClient(string query)
            {
                _query = query;
            }

            internal IReadOnlyList<TextSearchProvider.TextSearchResult> Results { get; private set; } = [];

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(messages);
                await Task.Yield();

                string responseId = Guid.NewGuid().ToString("N");

                IEnumerable<TextSearchProvider.TextSearchResult>? toolAnswer = messages
                    .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
                    .Select(content => content.Result)
                    .OfType<IEnumerable<TextSearchProvider.TextSearchResult>>()
                    .FirstOrDefault();

                if (toolAnswer is null)
                {
                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [new FunctionCallContent(
                            $"search-{responseId}",
                            "Search",
                            new Dictionary<string, object?>(StringComparer.Ordinal) { ["userQuestion"] = _query })])
                    {
                        ResponseId = responseId,
                        MessageId = responseId,
                    };
                    yield break;
                }

                Results = [.. toolAnswer];

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
                await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
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

            internal FixedTextChatClient(string text)
            {
                _text = text;
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(messages);
                await Task.Yield();

                string responseId = Guid.NewGuid().ToString("N");
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
                await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
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
}

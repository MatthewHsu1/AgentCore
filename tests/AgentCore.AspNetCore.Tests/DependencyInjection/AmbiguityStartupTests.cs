using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Knowledge;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentCore.AspNetCore.Tests.DependencyInjection;

/// <summary>
/// The <c>ambiguity:</c> warnings the validator raises, exercised through
/// <see cref="ConfigurationStartup.Load"/> and <see cref="AgentCoreBoot.BootAsync"/> so the
/// plumbing that carries them to the log is what is under test.
/// </summary>
public sealed class AmbiguityStartupTests
{
    [Fact]
    public async Task BootAsync_ASingleFacetAmbiguityDocument_LogsTheConfigurationWarningBelowTelemetry()
    {
        // A warning EvaluateStructure raised (K33 here) must reach the log, through
        // ConfigurationStartup.Load's returned Warnings and AgentCoreBoot's own logger.
        RecordingLoggerFactory loggers = new();

        AgentCoreOptions options = new()
        {
            Configuration = SingleFacetDocument(),
            LoggerFactory = loggers,
        };
        options.UseChatClients(_ => new RoutingChatClientFactory(new FragmentingChatClient("hello")));
        options.UseKnowledgeStores(new TestKnowledgeAdapter());

        await using var boot = new AgentCoreBoot(Options.Create(options), loggers);
        await boot.BootAsync(TestContext.Current.CancellationToken);

        var warnings = loggers.Lines.Where(line =>
            line.Level == LogLevel.Warning
            && line.Message.Contains("/providers/knowledge/ambiguity", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(warnings, line => line.Message.Contains("at most one", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_AGraphDocumentWithAmbiguity_ReturnsTheK39Warning()
    {
        // K39: channel 1 cannot fire on a graph: document, so ambiguity: there is a boot warning
        // that ConfigurationStartup.Load must not discard.
        var wildcard = new KnowledgeWildcardConfiguration { Value = "*", Facets = ["applies_to", "brand"] };
        StateSlotConfiguration facet = new() { Type = StateSlotType.String, Writer = StateWriter.Extractor, EnumValues = [] };

        var configuration = new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "graph-ambiguity",
            Graph = new GraphConfiguration(),
            Extractor = new ExtractorConfiguration { Model = new ModelReference { Ref = "small" } },
            State = new Dictionary<string, StateSlotConfiguration>(StringComparer.Ordinal)
            {
                ["applies_to"] = facet with { EnumValues = [JsonValue.Create("a")!] },
                ["brand"] = facet with { EnumValues = [JsonValue.Create("b")!] },
            },
            Providers = new ProvidersConfiguration
            {
                Llm = [new LlmProviderConfiguration { Kind = "openai", Model = "gpt", As = "small" }],
                Knowledge = new KnowledgeProviderConfiguration
                {
                    Kind = "qdrant",
                    Collection = "kb",
                    Fields = new KnowledgeFieldsConfiguration { Body = "text" },
                    Scope = new KnowledgeScopeConfiguration
                    {
                        Template = "facets.{key}",
                        FromState = ["applies_to", "brand"],
                        Wildcard = wildcard,
                    },
                    Ambiguity = new KnowledgeAmbiguityConfiguration(),
                },
            },
        };

        AgentCoreOptions options = new() { Configuration = configuration };

        var result = ConfigurationStartup.Load(options);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("/providers/knowledge/ambiguity", warning.Pointer);
    }

    private static AgentCoreConfiguration SingleFacetDocument()
        => new()
        {
            ApiVersion = "agentcore/v1",
            Name = "ambiguity-boot",
            Extractor = new ExtractorConfiguration { Model = new ModelReference { Ref = "fill" } },
            State = new Dictionary<string, StateSlotConfiguration>(StringComparer.Ordinal)
            {
                ["machine"] = new StateSlotConfiguration
                {
                    Type = StateSlotType.String,
                    Writer = StateWriter.Extractor,
                    EnumValues = [JsonValue.Create("ct900")!, JsonValue.Create("ct1200")!],
                },
            },
            Providers = new ProvidersConfiguration
            {
                Llm = [new LlmProviderConfiguration { Kind = "test", Model = "test", As = "fill" }],
                Knowledge = new KnowledgeProviderConfiguration
                {
                    Kind = "test",
                    Collection = "kb",
                    Fields = new KnowledgeFieldsConfiguration { Body = "body" },
                    Scope = new KnowledgeScopeConfiguration
                    {
                        Template = "facets.{key}",
                        FromState = ["machine"],
                        Wildcard = new KnowledgeWildcardConfiguration { Value = "*", Facets = ["machine"] },
                    },
                    Ambiguity = new KnowledgeAmbiguityConfiguration(),
                },
            },
            Policy = new PolicyConfiguration
            {
                Initial = "answering",
                Stages = [new StageConfiguration { Id = "answering", Agent = "resolver", Terminal = true }],
            },
            Agents = new AgentsConfiguration
            {
                Items =
                [
                    new AgentConfiguration
                    {
                        Id = "resolver",
                        Instructions = "I answer about one machine",
                        Knowledge = new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Prefetch, Scoped = true },
                    },
                ],
            },
        };

    /// <summary>Answers <c>providers.knowledge.kind: test</c> with a port that finds nothing.</summary>
    private sealed class TestKnowledgeAdapter : IKnowledgeStoreAdapter
    {
        public string Kind => "test";

        public bool CanServeSearch => true;

        public bool CanScope => true;

        public ValueTask<IKnowledgeRetrievalPort> CreateSearchAsync(
            KnowledgeProviderConfiguration entry,
            ISecretResolverPort? secrets,
            IEmbeddingGenerator<string, Embedding<float>>? embeddings,
            bool requireScope,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IKnowledgeRetrievalPort>(new EmptyPort());
    }

    private sealed class EmptyPort : IKnowledgeRetrievalPort
    {
        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);
    }
}

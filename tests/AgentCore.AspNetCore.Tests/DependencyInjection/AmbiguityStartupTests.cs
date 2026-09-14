using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Knowledge;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentCore.AspNetCore.Tests.DependencyInjection;

/// <summary>
/// The <c>ambiguity:</c> warning the validator raises, exercised through
/// <see cref="AgentCoreBoot.BootAsync"/> so the plumbing that carries it to the log is what is
/// under test.
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
            string query, KnowledgeScope? scope = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);
    }
}

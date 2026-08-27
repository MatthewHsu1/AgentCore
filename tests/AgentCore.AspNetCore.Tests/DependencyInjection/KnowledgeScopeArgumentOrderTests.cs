using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;
using AgentCore.Application.Secrets;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Knowledge;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentCore.AspNetCore.Tests.DependencyInjection;

/// <summary>
/// Which of the two scoping questions reaches which parameter, asserted from the composition root.
/// </summary>
/// <remarks>
/// <para>
/// <c>AgentCoreBoot</c> passes <c>AnyScoped</c> and then <c>AllScoped</c> into two adjacent
/// <see langword="bool"/> parameters of a security-adjacent pair. Swap them and the compiler is
/// silent, every other test in the tree stays green, and the deployment is wrong in both directions
/// at once: a mixed document makes the shared store strict, so every turn of its unscoped agent
/// throws, and a fully scoped document makes it permissive, so the store's own fail-closed guard
/// quietly disappears.
/// </para>
/// <para>
/// A MIXED document is the only one that can tell them apart — it is the one shape where the two
/// questions have different answers. So both facts here run on the same mixed document: one reads
/// what the adapter was handed, the other reads what the capability check did with the other value.
/// </para>
/// </remarks>
public sealed class KnowledgeScopeArgumentOrderTests
{
    [Fact]
    public async Task BootAsync_MixedScoping_LeavesTheSharedStorePermissive()
    {
        // requireScope is AllScoped, the SECOND argument. False here, because one agent is unscoped
        // and the store is shared: a strict store would throw on every turn that agent takes.
        ScopeRecordingAdapter adapter = new(canScope: true);

        await using var boot = Boot(adapter);
        await boot.BootAsync(TestContext.Current.CancellationToken);

        Assert.True(adapter.Built);
        Assert.False(adapter.RequireScope);
    }

    [Fact]
    public async Task BootAsync_MixedScopingOverAnAdapterThatCannotScope_FailsTheStart()
    {
        // scopeDeclared is AnyScoped, the FIRST argument. True here, because one agent IS scoped, so
        // an adapter that cannot filter must refuse the start rather than serve that agent every
        // customer's cards. Under a swap this value would be AllScoped -- false -- and the start would
        // succeed with the leak in it.
        ScopeRecordingAdapter adapter = new(canScope: false);

        await using var boot = Boot(adapter);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await boot.BootAsync(TestContext.Current.CancellationToken));

        Assert.Contains("scoped: true", failure.Message, StringComparison.Ordinal);
        Assert.False(adapter.Built);
    }

    /// <summary>
    /// The one document shape that can tell the two questions apart: one scoped agent, one not.
    /// </summary>
    /// <remarks>
    /// Built as an object rather than parsed from YAML, so it needs no <c>providers.call</c> and no
    /// <c>providers.speech</c> — the schema requires both once a <c>providers:</c> block exists, and a
    /// boot that had to register a call vendor and a speech vendor would be testing those instead.
    /// </remarks>
    private static AgentCoreConfiguration MixedScoping()
        => new()
        {
            ApiVersion = "agentcore/v1",
            Name = "scope-argument-order",
            Providers = new ProvidersConfiguration
            {
                Knowledge = new KnowledgeProviderConfiguration { Kind = "test", Collection = "kb" },
            },
            Policy = new PolicyConfiguration
            {
                Initial = "answering",
                Stages =
                [
                    new StageConfiguration
                    {
                        Id = "answering",
                        Agent = "resolver",
                        To = [new StageTransition { Stage = "reviewing" }],
                    },
                    new StageConfiguration { Id = "reviewing", Agent = "analyst", Terminal = true },
                ],
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
                    new AgentConfiguration
                    {
                        Id = "analyst",
                        Instructions = "I search every product on purpose",
                        Knowledge = new AgentKnowledgeConfiguration { Mode = KnowledgeMode.Prefetch, Scoped = false },
                    },
                ],
            },
        };

    private static AgentCoreBoot Boot(IKnowledgeStoreAdapter adapter)
    {
        AgentCoreOptions options = new() { Configuration = MixedScoping() };

        options.UseChatClients(_ => new RoutingChatClientFactory(new FragmentingChatClient("hello")));
        options.UseKnowledgeStores(adapter);

        return new AgentCoreBoot(Options.Create(options), NullLoggerFactory.Instance);
    }

    /// <summary>A knowledge vendor that records the scoping question it was handed.</summary>
    private sealed class ScopeRecordingAdapter(bool canScope) : IKnowledgeStoreAdapter
    {
        public string Kind => "test";

        public bool CanServeSearch => true;

        public bool CanScope { get; } = canScope;

        /// <summary>Gets whether the composition root ever asked this adapter to build.</summary>
        public bool Built { get; private set; }

        /// <summary>Gets the value of the second scoping argument, as it arrived.</summary>
        public bool RequireScope { get; private set; }

        public ValueTask<IKnowledgeRetrievalPort> CreateSearchAsync(
            KnowledgeProviderConfiguration entry,
            ISecretResolverPort? secrets,
            IEmbeddingGenerator<string, Embedding<float>>? embeddings,
            bool requireScope,
            CancellationToken cancellationToken = default)
        {
            Built = true;
            RequireScope = requireScope;
            return ValueTask.FromResult<IKnowledgeRetrievalPort>(new SilentPort());
        }
    }

    /// <summary>A port that answers with nothing. Nothing here searches.</summary>
    private sealed class SilentPort : IKnowledgeRetrievalPort
    {
        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);
    }
}

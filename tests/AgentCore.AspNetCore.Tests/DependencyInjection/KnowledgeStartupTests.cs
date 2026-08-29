using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.Application.Secrets;
using AgentCore.Domain.Knowledge;
using Xunit;

namespace AgentCore.AspNetCore.Tests.DependencyInjection;

/// <summary>
/// Step 3b of the composition root: open the knowledge port the document names, before any tool is
/// built.
/// </summary>
/// <remarks>
/// Task 1 deleted the four knowledge tools, and with them every host-DI test that used to observe
/// this wiring by calling a tool. This file proves the two seams directly, with no tool involved.
/// </remarks>
public sealed class KnowledgeStartupTests
{
    [Fact]
    public async Task OpenAsync_UseKnowledgeStoresWithAMatchingProvidersKnowledgeBlock_ReturnsALivePort()
    {
        RecordingAdapter adapter = new("test");
        AgentCoreOptions options = new();
        options.UseKnowledgeStores(adapter);

        var configuration = Configuration(new KnowledgeProviderConfiguration
        {
            Kind = "test",
            Collection = "manuals",
            Fields = new KnowledgeFieldsConfiguration { Body = "body" },
        });
        AgentCoreStartup startup = new(configuration, ResolvedSecrets.Empty);

        var port = await KnowledgeStartup.OpenAsync(
            configuration,
            options,
            startup,
            embeddings: null,
            scopeDeclared: false,
            requireScope: false,
            TestContext.Current.CancellationToken);

        Assert.NotNull(port);
        Assert.Same(adapter.LastBuilt, port);
        Assert.True(adapter.CreateSearchCalled);
    }

    [Fact]
    public async Task OpenAsync_UseKnowledgeRetrievalRegistered_InvokesItWithTheStartup()
    {
        FakePort fake = new();
        AgentCoreStartup? seen = null;
        AgentCoreOptions options = new();
        options.UseKnowledgeRetrieval(startup =>
        {
            seen = startup;
            return fake;
        });

        var configuration = Configuration(knowledge: null);
        AgentCoreStartup startup = new(configuration, ResolvedSecrets.Empty);

        var port = await KnowledgeStartup.OpenAsync(
            configuration,
            options,
            startup,
            embeddings: null,
            scopeDeclared: false,
            requireScope: false,
            TestContext.Current.CancellationToken);

        Assert.Same(fake, port);
        Assert.Same(startup, seen);
    }

    [Fact]
    public async Task OpenAsync_NoKnowledgeBlockAndNoAgentKnowledge_OpensNothingAndAsksNoAdapter()
    {
        // The hosting layer registers a knowledge adapter by default, so a document that never
        // mentions knowledge must not pay for a store — or fail over one it never asked for.
        RecordingAdapter adapter = new("test");
        AgentCoreOptions options = new();
        options.UseKnowledgeStores(adapter);

        var configuration = Configuration(knowledge: null);
        AgentCoreStartup startup = new(configuration, ResolvedSecrets.Empty);

        var port = await KnowledgeStartup.OpenAsync(
            configuration,
            options,
            startup,
            embeddings: null,
            scopeDeclared: false,
            requireScope: false,
            TestContext.Current.CancellationToken);

        Assert.Null(port);
        Assert.False(adapter.CreateSearchCalled);
    }

    [Fact]
    public async Task OpenAsync_AnAgentDeclaresKnowledgeButNoProvidersBlock_FailsTheStart()
    {
        // The agent's own knowledge: block is the other opt-in, so the registry IS reached -- and
        // then there is nothing to reach it with. AgentCore has no default vendor, collection or
        // payload shape, so the only honest answer is a refusal that names the missing block.
        RecordingAdapter adapter = new("qdrant");
        AgentCoreOptions options = new();
        options.UseKnowledgeStores(adapter);

        var configuration = Configuration(knowledge: null) with
        {
            Agents = new AgentsConfiguration
            {
                Items =
                [
                    new AgentConfiguration
                    {
                        Id = "only",
                        Instructions = "I answer everything",
                        Knowledge = new AgentKnowledgeConfiguration(),
                    },
                ],
            },
        };
        AgentCoreStartup startup = new(configuration, ResolvedSecrets.Empty);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await KnowledgeStartup.OpenAsync(
                configuration,
                options,
                startup,
                embeddings: null,
                scopeDeclared: false,
                requireScope: false,
                TestContext.Current.CancellationToken));

        Assert.Contains(failure.Errors, error => error.Pointer == "/providers/knowledge");
        Assert.False(adapter.CreateSearchCalled);
    }

    private static AgentCoreConfiguration Configuration(KnowledgeProviderConfiguration? knowledge)
        => new()
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Providers = new ProvidersConfiguration { Knowledge = knowledge },
        };

    /// <summary>An offline knowledge vendor that records what it was asked to build.</summary>
    private sealed class RecordingAdapter(string kind) : IKnowledgeStoreAdapter
    {
        public string Kind { get; } = kind;

        public bool CanServeSearch => true;

        public bool CanScope => true;

        public bool CreateSearchCalled { get; private set; }

        public IKnowledgeRetrievalPort? LastBuilt { get; private set; }

        public ValueTask<IKnowledgeRetrievalPort> CreateSearchAsync(
            KnowledgeProviderConfiguration entry,
            ISecretResolverPort? secrets,
            IEmbeddingGenerator<string, Embedding<float>>? embeddings,
            bool requireScope,
            CancellationToken cancellationToken = default)
        {
            CreateSearchCalled = true;
            LastBuilt = new FakePort();
            return ValueTask.FromResult(LastBuilt);
        }
    }

    /// <summary>A port that answers with nothing. Only its identity is asserted against.</summary>
    private sealed class FakePort : IKnowledgeRetrievalPort
    {
        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);
    }
}

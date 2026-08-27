using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Knowledge;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentCore.AspNetCore.Tests.DependencyInjection;

/// <summary>
/// That the composition root's own logger factory reaches the provider a shipped host compiles.
/// </summary>
/// <remarks>
/// <para>
/// The knowledge provider's own facts hand a recording factory straight into
/// <c>KnowledgeProviderFactory.Create</c>, and every compile-table fact builds an
/// <c>AgentCompilationContext</c> without a <c>Loggers</c> at all. So one line —
/// <c>CompilationStartup</c>'s <c>Loggers = loggers</c> — is what makes the whole observability fix
/// live in a real host, and deleting it left the entire suite green while a store outage went silent
/// again. That is the original defect restated, so it gets a fact of its own, taken through
/// <c>AgentCoreBoot</c> rather than through the compiler.
/// </para>
/// <para>
/// The assertion is on the ERROR row and not the debug one: it is the row that exists for an outage,
/// and it is the row a default production configuration is listening to.
/// </para>
/// </remarks>
public sealed class KnowledgeLoggingThroughBootTests
{
    [Fact]
    public async Task BootAsync_TheStoreIsDown_TheCompiledProviderWritesThroughTheHostsLoggerFactory()
    {
        RecordingLoggerFactory loggers = new();
        InvalidOperationException down = new("qdrant is down");

        await using var boot = Boot(new ThrowingPort(down), loggers);
        await boot.BootAsync(TestContext.Current.CancellationToken);

        // Run the retrieval the way the framework runs it, on the agent the boot actually compiled.
        var provider = Assert.Single(Providers(boot.Compiled.Agents["resolver"]).OfType<TextSearchProvider>());

#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        AIContextProvider.InvokingContext context = new(
            boot.Compiled.Agents["resolver"], null, new AIContext());
#pragma warning restore MAAI001
        await provider.InvokingAsync(context, TestContext.Current.CancellationToken);

        var line = Assert.Single(loggers.Of(12));
        Assert.Equal("resolver", line.Field<string>("Agent"));
        Assert.Same(down, line.Exception);
    }

    /// <summary>The document: one agent, one knowledge block, nothing else the boot must resolve.</summary>
    private static AgentCoreConfiguration OneScopelessReader()
        => new()
        {
            ApiVersion = "agentcore/v1",
            Name = "knowledge-logging-through-boot",
            Agents = new AgentsConfiguration
            {
                Items =
                [
                    new AgentConfiguration
                    {
                        Id = "resolver",
                        Instructions = "I answer from the knowledge base",

                        // scoped: false, so the provider's own gate cannot short-circuit ahead of the
                        // port and leave nothing for the failure row to report.
                        Knowledge = new AgentKnowledgeConfiguration
                        {
                            Mode = KnowledgeMode.Prefetch,
                            Scoped = false,
                        },
                    },
                ],
            },
        };

    private static AgentCoreBoot Boot(IKnowledgeRetrievalPort port, RecordingLoggerFactory loggers)
    {
        AgentCoreOptions options = new()
        {
            Configuration = OneScopelessReader(),

            // The seam the boot's own constructor prefers over the container's factory. Setting it
            // here is what makes "the host's factory" a thing this test can read back.
            LoggerFactory = loggers,
        };

        options.UseChatClients(_ => new RoutingChatClientFactory(new FragmentingChatClient("hello")));
        options.UseKnowledgeRetrieval(_ => port);

        return new AgentCoreBoot(Options.Create(options), NullLoggerFactory.Instance);
    }

    private static IEnumerable<AIContextProvider> Providers(AIAgent agent)
    {
        var inner = agent.GetService<ChatClientAgent>();
        Assert.NotNull(inner);
        return inner.AIContextProviders ?? [];
    }

    /// <summary>A store that is down, the way Qdrant is down.</summary>
    private sealed class ThrowingPort(Exception failure) : IKnowledgeRetrievalPort
    {
        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, CancellationToken cancellationToken = default)
            => throw failure;
    }
}

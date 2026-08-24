using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tools;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.DependencyInjection;

/// <summary>
/// Step 4 of the composition root: build every tool source's registrations into one registry, and
/// report back every source among them that the composition root must own.
/// </summary>
public sealed class ToolRegistryStartupTests
{
    [Fact]
    public async Task BuildAsync_ADocumentWithNoMcpBlockAndNoHostSource_OwnsNothing()
    {
        var built = await BuildAsync(NoMcpConfiguration(), new AgentCoreOptions());

        Assert.Empty(built.Owned);
    }

    [Fact]
    public async Task BuildAsync_TheBuilderThrowsAfterASourceAlreadyOpened_ClosesThatSourceBeforeRethrowing()
    {
        // Simulates the real failure this fix closes: a source's own ProvideAsync can succeed (it has
        // already opened whatever it opens) and the overall build can still fail later — here, on an
        // undeclared tool VerifyEveryDeclarationIsServed only catches once every source has answered.
        OpenTrackingToolSource source = new();
        AgentCoreOptions options = new();
        options.AddToolSource(_ => source);

        await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => BuildAsync(UndeclaredToolConfiguration(), options).AsTask());

        Assert.True(source.Disposed);
    }

    /// <summary>
    /// A <c>kind: agent</c> tool reaches no source (<see cref="ToolRegistryBuilder"/> carves it out of
    /// its own collision check), so without this guard a discovered id and a declared agent tool could
    /// claim the same served id: the registry would never notice, <c>ServedIds</c> would just union
    /// the two, and <c>ConfigurationCompiler</c> would find the declared entry first and silently hand
    /// the agent the delegation tool instead of the discovered one.
    /// </summary>
    [Fact]
    public async Task BuildAsync_ADiscoveredIdCollidesWithADeclaredAgentTool_FailsNamingTheId()
    {
        AgentCoreOptions options = new();
        options.AddToolSource(_ => new DiscoveringToolSource("shared_id"));

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => BuildAsync(CollidingAgentToolConfiguration(), options).AsTask());

        var error = Assert.Single(failure.Errors);
        Assert.Contains("shared_id", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// AgentCore.Hosting registers <c>McpToolSource</c> into <c>options.ToolSources</c>; a host that
    /// calls <c>AddAgentCoreAsync</c> directly never does. Without this guard, a document with an
    /// <c>mcp:</c> block and no registered source at all would boot clean and just serve fewer tools
    /// than it declares, with no error anywhere — the silent no-op this codebase rejects everywhere
    /// else. This deliberately checks a plain count, never <c>McpToolSource</c> by type: this project
    /// must not reference AgentCore.Infrastructure, where that type lives.
    /// </summary>
    [Fact]
    public async Task BuildAsync_AnMcpBlockWithNoRegisteredToolSource_FailsNamingHowToFixIt()
    {
        AgentCoreOptions options = new();

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => BuildAsync(McpWithNoSourceConfiguration(), options).AsTask());

        var error = Assert.Single(failure.Errors);
        Assert.Contains("AddAgentCoreHostAsync", error.Message, StringComparison.Ordinal);
        Assert.Contains("AddToolSource", error.Message, StringComparison.Ordinal);
    }

    private static AgentCoreConfiguration McpWithNoSourceConfiguration()
        => new()
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Mcp =
            [
                new McpServerConfiguration
                {
                    Id = "jira",
                    Transport = McpTransport.Stdio,
                    Command = ["npx"],
                    Allow = [new McpAllowEntry { Name = "*" }],
                },
            ],
        };

    private static AgentCoreConfiguration NoMcpConfiguration()
        => new() { ApiVersion = "agentcore/v1", Name = "test" };

    private static AgentCoreConfiguration UndeclaredToolConfiguration()
        => new()
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Tools = [new ToolConfiguration { Id = "no_such_tool", Kind = ToolKind.Binding }],
        };

    private static AgentCoreConfiguration CollidingAgentToolConfiguration()
        => new()
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Tools = [new ToolConfiguration { Id = "shared_id", Kind = ToolKind.Agent, Agent = "specialist" }],
        };

    private static ValueTask<ToolRegistryBuildResult> BuildAsync(
        AgentCoreConfiguration configuration, AgentCoreOptions options)
    {
        AgentCoreStartup startup = new(configuration, ResolvedSecrets.Empty);

        return ToolRegistryStartup.BuildAsync(
            options,
            startup,
            (null, null),
            new RoutingChatClientFactory(new FragmentingChatClient("hello")),
            configuration,
            TestContext.Current.CancellationToken);
    }

    /// <summary>A tool source that tracks whether it was disposed, and serves nothing.</summary>
    private sealed class OpenTrackingToolSource : IToolSource, IAsyncDisposable
    {
        /// <summary>Gets whether this source was disposed.</summary>
        public bool Disposed { get; private set; }

        public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
            ToolSourceContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ToolRegistration>>([]);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A tool source that serves one id, standing in for what an MCP server's discovery would supply.</summary>
    private sealed class DiscoveringToolSource(string id) : IToolSource
    {
        public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
            ToolSourceContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(
                [new ToolRegistration(id, "A tool discovered under the same id a declared tool claims.", () => AIFunctionFactory.Create(() => "ok", id))]);
    }
}

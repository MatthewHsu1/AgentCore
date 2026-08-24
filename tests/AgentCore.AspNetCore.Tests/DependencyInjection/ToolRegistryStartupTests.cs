using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tools;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
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

    private static AgentCoreConfiguration NoMcpConfiguration()
        => new() { ApiVersion = "agentcore/v1", Name = "test" };

    private static AgentCoreConfiguration UndeclaredToolConfiguration()
        => new()
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Tools = [new ToolConfiguration { Id = "no_such_tool", Kind = ToolKind.Binding }],
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
}

using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Secrets;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.AspNetCore.Tests.DependencyInjection;

/// <summary>
/// Step 4 of the composition root. A document's <c>mcp:</c> block, when present, must hand back the
/// <see cref="AgentCore.Infrastructure.Tools.McpToolSource"/> it built so the composition root can
/// own it: nothing else closes the child processes and sessions behind it.
/// </summary>
public sealed class ToolRegistryStartupTests
{
    [Fact]
    public async Task BuildAsync_ADocumentWithNoMcpBlock_BuildsNoMcpSource()
    {
        var built = await BuildAsync(NoMcpConfiguration());

        Assert.Null(built.McpSource);
    }

    private static AgentCoreConfiguration NoMcpConfiguration()
        => new() { ApiVersion = "agentcore/v1", Name = "test" };

    private static ValueTask<ToolRegistryBuildResult> BuildAsync(AgentCoreConfiguration configuration)
    {
        AgentCoreStartup startup = new(configuration, ResolvedSecrets.Empty);

        return ToolRegistryStartup.BuildAsync(
            new AgentCoreOptions(),
            startup,
            (null, null),
            new RoutingChatClientFactory(new FragmentingChatClient("hello")),
            configuration,
            TestContext.Current.CancellationToken);
    }
}

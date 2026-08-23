using System.Threading.Channels;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Tests.Tools.Fakes;
using AgentCore.Infrastructure.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Tools;

/// <summary>
/// Decision 13's <c>mcp:</c> block: connect to a declared server, list what it offers, keep only
/// what <c>allow:</c> pins, and serve the rest under decision 10's naming.
/// </summary>
/// <remarks>
/// Test 7 of the task brief (a document's own description overriding the server's) is intentionally
/// not written. <see cref="McpAllowEntry"/> and <see cref="McpServerConfiguration"/> — the whole
/// shape Task 1 built for <c>mcp:</c> — carry no per-tool description key for a document to write
/// into, so there is nothing to test against. This is a spec gap, not an implementation gap: the
/// owner ruled to ship without one, because an MCP server always supplies a description and
/// <see cref="ToolRegistryBuilder"/> already fails the boot on an empty one, so decision 3's purpose
/// is met without inventing config surface mid-stage. See <c>task-3-report.md</c>.
/// </remarks>
public sealed class McpToolSourceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProvideAsync_AnAllowedTool_IsServedUnderTheServerDottedName()
    {
        await using InProcessMcpServer fake = new("create_issue");
        await using McpToolSource source = new(_ => fake.ClientTransport);

        var registrations = await source.ProvideAsync(ContextFor(Jira(Allow("create_issue"))), Token);

        var registration = Assert.Single(registrations);
        Assert.Equal("jira.create_issue", registration.Id);
        Assert.Equal(InProcessMcpServer.DescriptionOf("create_issue"), registration.Description);
    }

    [Fact]
    public async Task ProvideAsync_AnAliasedTool_IsServedUnderTheAlias()
    {
        await using InProcessMcpServer fake = new("search_issues");
        await using McpToolSource source = new(_ => fake.ClientTransport);

        var registrations = await source.ProvideAsync(
            ContextFor(Jira([new McpAllowEntry { Name = "search_issues", As = "find_ticket" }])), Token);

        var registration = Assert.Single(registrations);
        Assert.Equal("find_ticket", registration.Id);
    }

    [Fact]
    public async Task ProvideAsync_AToolTheServerOffersButAllowDoesNot_IsNotServed()
    {
        await using InProcessMcpServer fake = new("create_issue", "delete_issue");
        await using McpToolSource source = new(_ => fake.ClientTransport);

        var registrations = await source.ProvideAsync(ContextFor(Jira(Allow("create_issue"))), Token);

        var registration = Assert.Single(registrations);
        Assert.Equal("jira.create_issue", registration.Id);
    }

    [Fact]
    public async Task ProvideAsync_AllowIsStar_ServesEveryToolTheServerOffers()
    {
        await using InProcessMcpServer fake = new("create_issue", "delete_issue");
        await using McpToolSource source = new(_ => fake.ClientTransport);

        var registrations = await source.ProvideAsync(ContextFor(Jira(Allow("*"))), Token);

        Assert.Equal(2, registrations.Count);
        Assert.Contains(registrations, r => r.Id == "jira.create_issue");
        Assert.Contains(registrations, r => r.Id == "jira.delete_issue");
    }

    [Fact]
    public async Task ProvideAsync_AnAllowEntryTheServerDoesNotOffer_FailsTheBoot()
    {
        await using InProcessMcpServer fake = new("create_issue");
        await using McpToolSource source = new(_ => fake.ClientTransport);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await source.ProvideAsync(ContextFor(Jira(Allow("no_such_tool"))), Token));

        Assert.Contains("jira", failure.Message, StringComparison.Ordinal);
        Assert.Contains("no_such_tool", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvideAsync_AnUnreachableServer_FailsTheBootNamingTheServer()
    {
        await using McpToolSource source = new(_ => new ThrowingTransport());

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await source.ProvideAsync(ContextFor(Jira(Allow("create_issue"))), Token));

        Assert.Contains("jira", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeAsync_AfterProvide_ClosesTheClient()
    {
        // Measured against the real SDK (see task-3-report.md, Step 2): disposing McpClient does not
        // complete the streams a StreamClientTransport was given, and a call made on a disposed
        // client hangs forever rather than failing — neither of the two signals the brief suggests
        // is observable here. What IS real and measured: McpClient.DisposeAsync disposes the
        // ITransport session it connected through. This decorator observes that, one layer below
        // McpToolSource, without any hook into production code.
        await using InProcessMcpServer fake = new("create_issue");
        DisposalObservingTransport transport = new(fake.ClientTransport);
        McpToolSource source = new(_ => transport);

        await source.ProvideAsync(ContextFor(Jira(Allow("create_issue"))), Token);
        Assert.False(transport.Disposed);

        await source.DisposeAsync();

        Assert.True(transport.Disposed);
    }

    private static McpServerConfiguration Jira(IReadOnlyList<McpAllowEntry> allow)
        => new()
        {
            Id = "jira",
            Transport = McpTransport.Stdio,
            Command = ["jira-mcp"],
            Allow = allow,
        };

    private static McpAllowEntry[] Allow(params string[] names)
        => [.. names.Select(name => new McpAllowEntry { Name = name })];

    private static ToolSourceContext ContextFor(McpServerConfiguration server)
        => new(new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Mcp = [server],
        });

    /// <summary>A transport whose connection always fails, for the unreachable-server test.</summary>
    private sealed class ThrowingTransport : IClientTransport
    {
        public string Name => "throwing";

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("the server refused the connection");
    }

    /// <summary>Wraps a transport to record whether the session it connects is ever disposed.</summary>
    private sealed class DisposalObservingTransport(IClientTransport inner) : IClientTransport
    {
        public bool Disposed { get; private set; }

        public string Name => inner.Name;

        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken)
        {
            var session = await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new ObservedSession(session, () => Disposed = true);
        }

        private sealed class ObservedSession(ITransport inner, Action onDisposed) : ITransport
        {
            public string? SessionId => inner.SessionId;

            public ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;

            public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken)
                => inner.SendMessageAsync(message, cancellationToken);

            public async ValueTask DisposeAsync()
            {
                onDisposed();
                await inner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

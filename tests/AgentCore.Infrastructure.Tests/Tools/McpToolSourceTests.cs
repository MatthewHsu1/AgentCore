using AgentCore.Application.Tools.Registry;
using System.ComponentModel;
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
/// server's description is the only source a <c>kind: mcp</c> tool ever gets, MCP makes that
/// description optional (a tool can offer none at all), and a document has no override today. The
/// owner ruled to ship without one — an empty description still fails the boot, just from
/// <see cref="McpToolSource"/> itself rather than from <see cref="ToolRegistryBuilder"/>, because
/// the builder's own message tells a deployer to write a <c>description:</c> the <c>mcp:</c> shape
/// has nowhere to hold. See <c>task-3-report.md</c>.
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
    public async Task ProvideAsync_AnUnreachableServer_FailsTheBootNamingTheServerAndTheCause()
    {
        await using McpToolSource source = new(_ => new ThrowingTransport());

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await source.ProvideAsync(ContextFor(Jira(Allow("create_issue"))), Token));

        Assert.Contains("jira", failure.Message, StringComparison.Ordinal);

        // ToolSourceError.Fail(string) has no room for an inner exception, so the deployer only ever
        // learns the real cause if it is folded into the message text itself — the SDK's own
        // "Failed to connect transport." says nothing a deployer can act on.
        Assert.Contains("No such file or directory", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The server's own tool collection refuses a same-named second entry, so this is the one route
    /// left to reproduce a <c>tools/list</c> answer that repeats a name: the listing itself succeeds,
    /// and the client's own <c>ToDictionary</c> is what fails afterward. The message must say the
    /// listing succeeded, not that the connection "failed before it could list what it offers" — that
    /// wording would be false here.
    /// </summary>
    [Fact]
    public async Task ProvideAsync_TheServerListsOneNameTwice_FailsTheBootSayingListingSucceeded()
    {
        await using var fake = InProcessMcpServer.OfferingTheSameToolNameTwice("create_issue");
        await using McpToolSource source = new(_ => fake.ClientTransport);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await source.ProvideAsync(ContextFor(Jira(Allow("*"))), Token));

        Assert.Contains("jira", failure.Message, StringComparison.Ordinal);
        Assert.Contains("listed its tools", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("failed before it could list", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeAsync_AfterProvide_ClosesTheClient()
    {
        // Measured against the real SDK: disposing McpClient does not complete the streams a
        // StreamClientTransport was given, and a call made on a disposed client hangs forever rather
        // than failing — neither signal is observable here.
        await using InProcessMcpServer fake = new("create_issue");
        DisposalObservingTransport transport = new(fake.ClientTransport);
        McpToolSource source = new(_ => transport);

        await source.ProvideAsync(ContextFor(Jira(Allow("create_issue"))), Token);
        Assert.False(transport.Disposed);

        await source.DisposeAsync();

        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task ProvideAsync_AToolWithNoDescription_FailsTheBoot()
    {
        await using var fake = InProcessMcpServer.OfferingAToolWithNoDescription("create_issue");
        await using McpToolSource source = new(_ => fake.ClientTransport);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await source.ProvideAsync(ContextFor(Jira(Allow("create_issue"))), Token));

        Assert.Contains("jira", failure.Message, StringComparison.Ordinal);
        Assert.Contains("create_issue", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvideAsync_StarAlongsideANamedEntry_FailsTheBoot()
    {
        await using InProcessMcpServer fake = new("create_issue", "search_issues");
        await using McpToolSource source = new(_ => fake.ClientTransport);

        McpAllowEntry[] allow =
        [
            new() { Name = "*" },
            new() { Name = "search_issues", As = "find_ticket" },
        ];

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await source.ProvideAsync(ContextFor(Jira(allow)), Token));

        Assert.Contains("jira", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvideAsync_StarWithAnAlias_FailsTheBoot()
    {
        await using InProcessMcpServer fake = new("create_issue");
        await using McpToolSource source = new(_ => fake.ClientTransport);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await source.ProvideAsync(
                ContextFor(Jira([new McpAllowEntry { Name = "*", As = "everything" }])), Token));

        Assert.Contains("jira", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvideAsync_EmptyAllow_FailsTheBootNamingTheServer()
    {
        await using InProcessMcpServer fake = new("create_issue");
        await using McpToolSource source = new(_ => fake.ClientTransport);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await source.ProvideAsync(ContextFor(Jira([])), Token));

        Assert.Contains("jira", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One client's own <c>DisposeAsync</c> throwing must not abandon the rest, and must not stop
    /// <c>_clients.Clear()</c> from running: the host's own shutdown calls <c>DisposeAsync</c> exactly
    /// once, but this source's own failure path (<see cref="ProvideAsync"/> on a later server) already
    /// disposes everything once, so a clean second call must find nothing left to re-touch.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_WhenOneClientThrows_StillDisposesTheOthersAndStaysIdempotent()
    {
        await using InProcessMcpServer jiraFake = new("create_issue");
        await using InProcessMcpServer githubFake = new("open_pr");

        CountingTransport throwing = new(jiraFake.ClientTransport, throwOnDispose: true);
        CountingTransport clean = new(githubFake.ClientTransport);

        McpToolSource source = new(server => server.Id == "jira" ? throwing : clean);

        await source.ProvideAsync(
            ContextFor(Jira(Allow("create_issue")), ServerConfig("github", Allow("*"))), Token);

        var exception = await Record.ExceptionAsync(async () => await source.DisposeAsync());

        Assert.Null(exception);
        Assert.Equal(1, throwing.DisposeCount);
        Assert.Equal(1, clean.DisposeCount);

        await source.DisposeAsync();

        Assert.Equal(1, throwing.DisposeCount);
        Assert.Equal(1, clean.DisposeCount);
    }

    [Fact]
    public async Task ProvideAsync_TheSecondServerFails_DisposesTheFirstServersClient()
    {
        await using InProcessMcpServer fake = new("create_issue");
        DisposalObservingTransport firstTransport = new(fake.ClientTransport);
        McpToolSource source = new(server => server.Id == "jira" ? firstTransport : new ThrowingTransport());

        await Assert.ThrowsAsync<ConfigurationLoadException>(async () => await source.ProvideAsync(
            ContextFor(Jira(Allow("create_issue")), ServerConfig("github", Allow("*"))), Token));

        Assert.True(firstTransport.Disposed);
    }

    private static McpServerConfiguration Jira(IReadOnlyList<McpAllowEntry> allow)
        => ServerConfig("jira", allow);

    private static McpServerConfiguration ServerConfig(string id, IReadOnlyList<McpAllowEntry> allow)
        => new()
        {
            Id = id,
            Transport = McpTransport.Stdio,
            Command = [$"{id}-mcp"],
            Allow = allow,
        };

    private static McpAllowEntry[] Allow(params string[] names)
        => [.. names.Select(name => new McpAllowEntry { Name = name })];

    private static ToolSourceContext ContextFor(params McpServerConfiguration[] servers)
        => new(new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Mcp = servers,
        });

    /// <summary>
    /// A transport whose connection always fails, wrapping a cause the way a real transport failure
    /// (a missing stdio executable) would: the SDK's own message names no cause, and the real one —
    /// here, a stand-in for "No such file or directory" — sits in <see cref="Exception.InnerException"/>.
    /// </summary>
    private sealed class ThrowingTransport : IClientTransport
    {
        public string Name => "throwing";

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "Failed to connect transport.",
                new Win32Exception(2, "No such file or directory"));
    }

    /// <summary>Wraps a transport to count how many times the session it connects is disposed, optionally throwing on each.</summary>
    private sealed class CountingTransport(IClientTransport inner, bool throwOnDispose = false) : IClientTransport
    {
        public int DisposeCount { get; private set; }

        public string Name => inner.Name;

        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken)
        {
            var session = await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new CountingSession(session, this, throwOnDispose);
        }

        private sealed class CountingSession(ITransport inner, CountingTransport owner, bool throwOnDispose) : ITransport
        {
            public string? SessionId => inner.SessionId;

            public ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;

            public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken)
                => inner.SendMessageAsync(message, cancellationToken);

            public async ValueTask DisposeAsync()
            {
                owner.DisposeCount++;
                await inner.DisposeAsync().ConfigureAwait(false);
                if (throwOnDispose)
                {
                    throw new InvalidOperationException("the session failed to close.");
                }
            }
        }
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

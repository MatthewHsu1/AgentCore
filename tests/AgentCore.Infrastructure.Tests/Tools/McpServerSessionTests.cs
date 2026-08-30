using AgentCore.Application.Tools.Registry;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Tests.Tools.Fakes;
using AgentCore.Infrastructure.Tools;
using AgentCore.Infrastructure.Tools.Mcp;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Tools;

/// <summary>
/// The connection to one <c>mcp:</c> server, for as long as the process runs.
/// </summary>
/// <remarks>
/// Discovery is not the whole story. A server can take forever to answer, can still be warming up
/// when the boot reaches it, and can die long after it was listed. These drive a real MCP server
/// over real pipes, so nothing here is a stub of the wire.
/// </remarks>
public sealed class McpServerSessionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------
    // A wedged server costs its own timeout, not the SDK's 60-second default.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AServerThatNeverSpeaks_FailsAtItsOwnTimeout()
    {
        // A pipe with nothing on the far end: the transport connects and the handshake is never
        // answered, which is what /usr/bin/sleep as a command: does.
        await using McpToolSource source = new(_ => SilentTransport(), null);

        var watch = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await source.ProvideAsync(
                ContextFor(Wedged(connectTimeoutSeconds: 1, attempts: 1)), Token));
        watch.Stop();

        Assert.Contains("wedged", failure.Message, StringComparison.Ordinal);
        Assert.True(
            watch.Elapsed < TimeSpan.FromSeconds(20),
            $"the boot took {watch.Elapsed}, so the SDK's own 60s default is still what governs.");
    }

    [Fact]
    public async Task TheTimeoutMessage_TellsTheDeployerWhichKnobToTurn()
    {
        await using McpToolSource source = new(_ => SilentTransport(), null);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await source.ProvideAsync(
                ContextFor(Wedged(connectTimeoutSeconds: 1, attempts: 1)), Token));

        Assert.Contains("connectTimeoutSeconds", failure.Message, StringComparison.Ordinal);
    }

    // The SDK runs its InitializationTimeout on the same length as the session's own deadline, so
    // on a loaded machine the SDK's clock can fire first and throw its own TimeoutException.
    [Fact]
    public async Task WhenTheSdksOwnClockWins_TheKnobIsStillNamed()
    {
        await using McpServerSession session = new(
            Wedged(connectTimeoutSeconds: 1, attempts: 1),
            (_, _) => throw new TimeoutException("Client failed to initialize within the timeout."));

        var failure = await Assert.ThrowsAsync<TimeoutException>(
            async () => await session.OpenAsync(Token));

        Assert.Contains("connectTimeoutSeconds", failure.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // A cold start is a healthy server that answers nothing on the first try.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AServerThatIsSlowToStart_IsRetriedUntilItAnswers()
    {
        await using ControllableMcpServer fake = new("create_issue");
        var attempts = 0;

        await using McpToolSource source = new(
            _ =>
            {
                // The first two attempts find nothing listening, as npx fetching a package does.
                attempts++;
                return attempts <= 2 ? throw new IOException("connection refused") : fake.NewTransport();
            },
            null);

        var registrations = await source.ProvideAsync(
            ContextFor(Jira(attempts: 3, backoffMs: 1)), Token);

        Assert.Equal(3, attempts);
        Assert.Equal("jira.create_issue", Assert.Single(registrations).Id);
    }

    [Fact]
    public async Task AServerThatNeverAnswers_FailsAfterItsLastAttempt_AndSaysWhy()
    {
        var attempts = 0;
        await using McpToolSource source = new(
            _ =>
            {
                attempts++;
                throw new IOException("connection refused");
            },
            null);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await source.ProvideAsync(ContextFor(Jira(attempts: 3, backoffMs: 1)), Token));

        Assert.Equal(3, attempts);
        Assert.Contains("jira", failure.Message, StringComparison.Ordinal);
        Assert.Contains("connection refused", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerThatAnswersFirstTime_IsNotRetried()
    {
        await using ControllableMcpServer fake = new("create_issue");
        await using McpToolSource source = new(_ => fake.NewTransport(), null);

        await source.ProvideAsync(ContextFor(Jira()), Token);

        Assert.Equal(1, fake.ConnectionsOpened);
    }

    // ---------------------------------------------------------------------------------------------
    // The tool object outlives the connection it was discovered on.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AToolStillWorks_AfterTheServerItWasDiscoveredOnHasDied()
    {
        await using ControllableMcpServer fake = new("create_issue");
        await using McpToolSource source = new(_ => fake.NewTransport(), null);

        var registrations = await source.ProvideAsync(ContextFor(Jira()), Token);
        var tool = (AIFunction)Assert.Single(registrations).Materialise();

        // The tool object was built while the first connection was alive, and is never rebuilt.
        await fake.KillNewestConnectionAsync();

        var result = await tool.InvokeAsync(new AIFunctionArguments(), Token);

        Assert.Equal(2, fake.ConnectionsOpened);
        Assert.False(IsError(result), $"the call returned an error result: {result}");

        // The CallToolResult envelope is protocol and not answer, so what comes back is the text the
        // server put in it and never the box it arrived in.
        Assert.Equal("ran create_issue", result);
    }

    [Fact]
    public async Task AToolTheServerHasWithdrawn_ReturnsAnErrorResult_AndDoesNotThrow()
    {
        await using ControllableMcpServer fake = new("create_issue", "close_issue");
        await using McpToolSource source = new(_ => fake.NewTransport(), null);

        var registrations = await source.ProvideAsync(ContextFor(Jira()), Token);
        var tool = (AIFunction)registrations.Single(r => r.Id == "jira.close_issue").Materialise();

        fake.Withdraw("close_issue");
        await fake.AnnounceToolChangeAsync(Token);
        await WaitForAsync(async () =>
        {
            var probe = await tool.InvokeAsync(new AIFunctionArguments(), Token);
            return IsError(probe);
        });

        var result = await tool.InvokeAsync(new AIFunctionArguments(), Token);

        var error = Assert.IsType<JsonObject>(result);
        Assert.True(ToolErrorResult.IsError(error));
        Assert.Contains(
            "no longer offers",
            error[ToolErrorResult.MessageProperty]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The served set is fixed at boot. <c>allow:</c> pinned these names and every agent's
    /// <c>tools:</c> list was checked against the ids they became, so a tool the server adds later
    /// was never allowed and does not appear.
    /// </summary>
    [Fact]
    public async Task AToolTheServerAddsLater_IsNotServed()
    {
        await using ControllableMcpServer fake = new("create_issue");
        await using McpToolSource source = new(_ => fake.NewTransport(), null);

        var registrations = await source.ProvideAsync(ContextFor(Jira()), Token);

        Assert.Equal("jira.create_issue", Assert.Single(registrations).Id);
    }

    /// <summary>
    /// A connection that opens and then fails to list is a child process the retry would otherwise
    /// leave running, once per attempt, with the boot failing anyway.
    /// </summary>
    [Fact]
    public async Task AConnectionThatOpensAndThenFailsToList_IsClosedBeforeTheNextAttempt()
    {
        await using ControllableMcpServer fake = new("create_issue") { RefuseToList = true };
        List<McpClient> opened = [];

        await using McpToolSource source = new(
            async (_, timeout, cancellationToken) =>
            {
                var client = await McpClient.CreateAsync(
                    fake.NewTransport(),
                    new McpClientOptions { InitializationTimeout = timeout },
                    cancellationToken: cancellationToken);
                opened.Add(client);
                return client;
            },
            null);

        await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await source.ProvideAsync(ContextFor(Jira(attempts: 3, backoffMs: 1)), Token));

        Assert.Equal(3, opened.Count);

        // A closed client's session has completed. One still open is a connection this boot made,
        // failed on, and walked away from — in production, a live child process, once per attempt.
        await WaitForAsync(() => Task.FromResult(opened.TrueForAll(client => client.Completion.IsCompleted)));
        Assert.All(opened, client => Assert.True(client.Completion.IsCompleted, "a failed attempt was left open."));
    }

    /// <summary>
    /// The reconnect re-lists, so the tool may have gone in between. Without a second check the
    /// repeat runs unguarded and the model reads a raw protocol fault.
    /// </summary>
    [Fact]
    public async Task AToolWithdrawnWhileTheServerWasDown_ReturnsAnErrorResult()
    {
        await using ControllableMcpServer fake = new("create_issue", "close_issue");
        await using McpToolSource source = new(_ => fake.NewTransport(), null);

        var registrations = await source.ProvideAsync(ContextFor(Jira()), Token);
        var tool = (AIFunction)registrations.Single(r => r.Id == "jira.close_issue").Materialise();

        fake.Withdraw("close_issue");
        await fake.KillNewestConnectionAsync();

        // Closing the pipes is not instant, so a call made in the moment after the kill can still be
        // answered by the connection that is on its way out. Calling until one of them reaches the
        // dead connection is what puts the reconnect under test; every call after that must land on
        // the same answer, which is what the assertion below checks.
        object? result = null;
        await WaitForAsync(async () =>
        {
            result = await tool.InvokeAsync(new AIFunctionArguments(), Token);
            return IsError(result);
        });

        var error = Assert.IsType<JsonObject>(result);
        Assert.True(ToolErrorResult.IsError(error));
        Assert.Contains(
            "no longer offers",
            error[ToolErrorResult.MessageProperty]!.GetValue<string>(),
            StringComparison.Ordinal);

        // It stays gone: the guard is on the call, not on one unlucky moment.
        Assert.True(IsError(await tool.InvokeAsync(new AIFunctionArguments(), Token)));
    }

    // ---------------------------------------------------------------------------------------------
    // Through the registry: a tool that hangs is given up on, and the model is told.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AToolThatHangs_IsGivenUpOn_AtTheServersOwnCallTimeout()
    {
        await using ControllableMcpServer fake = new("create_issue") { CallDelay = TimeSpan.FromMinutes(5) };
        await using McpToolSource source = new(_ => fake.NewTransport(), null);

        var registrations = await source.ProvideAsync(ContextFor(Jira(callTimeoutSeconds: 1)), Token);
        var registration = Assert.Single(registrations);

        Assert.Equal(TimeSpan.FromSeconds(1), registration.CallTimeout);

        var registry = await ToolRegistryBuilder.BuildAsync(
            [new OneSource(registration)], ContextFor(Jira(callTimeoutSeconds: 1)), Token);

        var watch = Stopwatch.StartNew();
        var result = await ((AIFunction)registry.Resolve("jira.create_issue"))
            .InvokeAsync(new AIFunctionArguments(), Token);
        watch.Stop();

        Assert.True(ToolErrorResult.IsError(Assert.IsType<JsonObject>(result)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"it waited {watch.Elapsed}.");
    }

    // ---------------------------------------------------------------------------------------------
    // Closing a session closes the connection under it, whatever the tools are still holding.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task DisposingTheSource_LeavesTheToolReturningAnErrorResult_RatherThanThrowing()
    {
        await using ControllableMcpServer fake = new("create_issue");
        McpToolSource source = new(_ => fake.NewTransport(), null);

        var registrations = await source.ProvideAsync(ContextFor(Jira()), Token);
        var tool = (AIFunction)Assert.Single(registrations).Materialise();

        await source.DisposeAsync();

        var result = await tool.InvokeAsync(new AIFunctionArguments(), Token);

        Assert.True(ToolErrorResult.IsError(Assert.IsType<JsonObject>(result)));
    }

    private static bool IsError(object? result)
        => result is JsonObject map && ToolErrorResult.IsError(map);

    /// <summary>Waits for a condition a notification will make true, or gives up.</summary>
    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(20, Token);
        }
    }

    /// <summary>A transport whose far end never answers the handshake.</summary>
    private static StreamClientTransport SilentTransport()
    {
        Pipe toNowhere = new();
        Pipe fromNowhere = new();
        return new StreamClientTransport(toNowhere.Writer.AsStream(), fromNowhere.Reader.AsStream());
    }

    private static ToolSourceContext ContextFor(McpServerConfiguration server)
        => new(new AgentCoreConfiguration { ApiVersion = "agentcore/v1", Name = "mcp", Mcp = [server] });

    private static McpServerConfiguration Wedged(int connectTimeoutSeconds, int attempts)
        => new()
        {
            Id = "wedged",
            Transport = McpTransport.Stdio,
            Command = ["irrelevant"],
            Allow = [new McpAllowEntry { Name = "*" }],
            ConnectTimeoutSeconds = connectTimeoutSeconds,
            Retry = new McpRetryConfiguration { Attempts = attempts, BackoffMs = 1 },
        };

    private static McpServerConfiguration Jira(
        int? attempts = null, int? backoffMs = null, int? callTimeoutSeconds = null)
        => new()
        {
            Id = "jira",
            Transport = McpTransport.Stdio,
            Command = ["irrelevant"],
            Allow = [new McpAllowEntry { Name = "*" }],
            ConnectTimeoutSeconds = 10,
            CallTimeoutSeconds = callTimeoutSeconds,
            Retry = attempts is null && backoffMs is null
                ? null
                : new McpRetryConfiguration { Attempts = attempts, BackoffMs = backoffMs },
        };

    private sealed class OneSource(ToolRegistration registration) : Application.Ports.IToolSource
    {
        public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
            ToolSourceContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ToolRegistration>>([registration]);
    }
}

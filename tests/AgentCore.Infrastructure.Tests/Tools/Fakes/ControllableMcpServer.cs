using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCore.Infrastructure.Tests.Tools.Fakes;

/// <summary>
/// A real MCP server that can be made to misbehave, one connection at a time.
/// </summary>
/// <remarks>
/// <see cref="InProcessMcpServer"/> answers one connection and never changes. The session tests need
/// the opposite: a server that refuses the first attempts, that dies between two calls, and that
/// withdraws a tool it had offered. Each <see cref="NewTransport"/> is a fresh pipe pair and a fresh
/// <see cref="McpServer"/>, which is what a real reconnect gets.
/// </remarks>
internal sealed class ControllableMcpServer : IAsyncDisposable
{
    private readonly List<Connection> _servers = [];

    private readonly Lock _sync = new();

    private List<string> _tools;

    /// <summary>One connection: the server, and the pipes a dead process would close behind it.</summary>
    private sealed record Connection(McpServer Server, Pipe ClientToServer, Pipe ServerToClient);

    /// <summary>Starts a server offering the tools named.</summary>
    /// <param name="toolNames">The tools <c>tools/list</c> reports.</param>
    public ControllableMcpServer(params string[] toolNames) => _tools = [.. toolNames];

    /// <summary>Gets how many connections have been opened against this server.</summary>
    public int ConnectionsOpened { get; private set; }

    /// <summary>Gets or sets how long a tool call takes before it answers.</summary>
    public TimeSpan CallDelay { get; set; }

    /// <summary>
    /// Gets or sets whether <c>tools/list</c> fails, so a connection opens and then goes no further.
    /// </summary>
    public bool RefuseToList { get; set; }

    /// <summary>Gets the description this server gives a tool of one name.</summary>
    /// <param name="toolName">The tool name.</param>
    /// <returns>The description.</returns>
    public static string DescriptionOf(string toolName) => $"The '{toolName}' tool.";

    /// <summary>Opens a fresh transport, backed by a fresh server.</summary>
    /// <returns>The transport a client connects through.</returns>
    public IClientTransport NewTransport()
    {
        Pipe clientToServer = new();
        Pipe serverToClient = new();

        McpServerOptions options = new();
        options.Handlers.ListToolsHandler = (_, _) => RefuseToList
            ? throw new InvalidOperationException("this server will not say what it offers")
            : ValueTask.FromResult(new ListToolsResult
            {
                Tools = [.. Offered().Select(name => new Tool
                {
                    Name = name,
                    Description = DescriptionOf(name),
                })],
            });

        options.Handlers.CallToolHandler = async (request, ct) =>
        {
            if (CallDelay > TimeSpan.Zero)
            {
                await Task.Delay(CallDelay, ct);
            }

            return new CallToolResult { Content = [new TextContentBlock { Text = $"ran {request.Params?.Name}" }] };
        };

        var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            options);

        _ = server.RunAsync();

        lock (_sync)
        {
            _servers.Add(new Connection(server, clientToServer, serverToClient));
            ConnectionsOpened++;
        }

        return new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream());
    }

    /// <summary>Stops offering one tool, without telling anybody.</summary>
    /// <param name="toolName">The tool to withdraw.</param>
    public void Withdraw(string toolName)
    {
        lock (_sync)
        {
            _tools = [.. _tools.Where(name => name != toolName)];
        }
    }

    /// <summary>Tells every open connection that the tool list has changed.</summary>
    /// <param name="cancellationToken">Cancels the send.</param>
    public async ValueTask AnnounceToolChangeAsync(CancellationToken cancellationToken)
    {
        Connection[] open;
        lock (_sync)
        {
            open = [.. _servers];
        }

        foreach (var connection in open)
        {
            await connection.Server.SendNotificationAsync(
                NotificationMethods.ToolListChangedNotification, cancellationToken);
        }
    }

    /// <summary>Drops the newest connection, as a crashed child process would.</summary>
    public async ValueTask KillNewestConnectionAsync()
    {
        Connection newest;
        lock (_sync)
        {
            newest = _servers[^1];
            _servers.RemoveAt(_servers.Count - 1);
        }

        await CloseAsync(newest);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Connection[] open;
        lock (_sync)
        {
            open = [.. _servers];
            _servers.Clear();
        }

        foreach (var connection in open)
        {
            await CloseAsync(connection);
        }
    }

    /// <summary>
    /// Closes one connection the way the operating system closes a dead process's: the pipes end,
    /// not just the server object.
    /// </summary>
    /// <remarks>
    /// Disposing the <see cref="McpServer"/> alone leaves both pipes open, so the client never reads
    /// end-of-stream and its session never completes — it simply waits forever for a reply that is
    /// not coming. A real child process cannot leave its handles open after it dies, and a fake that
    /// does hides exactly the failure these tests exist to catch.
    /// </remarks>
    private static async ValueTask CloseAsync(Connection connection)
    {
        try
        {
            await connection.Server.DisposeAsync();
        }
        catch
        {
        }

        await connection.ServerToClient.Writer.CompleteAsync();
        await connection.ServerToClient.Reader.CompleteAsync();
        await connection.ClientToServer.Writer.CompleteAsync();
        await connection.ClientToServer.Reader.CompleteAsync();
    }

    private List<string> Offered()
    {
        lock (_sync)
        {
            return _tools;
        }
    }
}

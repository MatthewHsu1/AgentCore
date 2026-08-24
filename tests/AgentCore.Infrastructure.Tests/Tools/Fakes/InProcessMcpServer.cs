using System.IO.Pipelines;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCore.Infrastructure.Tests.Tools.Fakes;

/// <summary>
/// A real MCP server, wired to a real MCP client over two in-process pipes.
/// </summary>
/// <remarks>
/// <c>McpToolSourceTests</c> needs the real protocol — real initialization, capability negotiation,
/// and <c>tools/list</c> — without launching a child process. This wires
/// <see cref="StreamServerTransport"/> to <see cref="StreamClientTransport"/> over two
/// <see cref="Pipe"/>s, so nothing here is a stub of the wire format.
/// </remarks>
internal sealed class InProcessMcpServer : IAsyncDisposable
{
    private readonly McpServer _server;

    /// <summary>Starts a server offering one tool per name given, each with a fixed description.</summary>
    /// <param name="toolNames">The tools the server lists.</param>
    public InProcessMcpServer(params string[] toolNames)
        : this([.. toolNames.Select(name => (name, (string?)DescriptionOf(name)))])
    {
    }

    /// <summary>Starts a server offering exactly the tools given, each with its own description.</summary>
    /// <param name="tools">
    /// Each tool's name and description. A <see langword="null"/> description is never sent at all —
    /// MCP makes it optional — which is how <see cref="OfferingAToolWithNoDescription"/> reproduces a
    /// server that describes nothing.
    /// </param>
    /// <param name="duplicateToolName">
    /// A tool name to list a second time through <see cref="McpServerHandlers.ListToolsHandler"/>, or
    /// <see langword="null"/> for none. <see cref="McpServerPrimitiveCollection{T}"/> itself refuses
    /// two tools of one name, so this is the only route left to reproduce a server whose
    /// <c>tools/list</c> answer repeats a name — <see cref="OfferingTheSameToolNameTwice"/> uses it.
    /// </param>
    private InProcessMcpServer(IReadOnlyList<(string Name, string? Description)> tools, string? duplicateToolName = null)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());

        var options = new McpServerOptions { ToolCollection = new McpServerPrimitiveCollection<McpServerTool>() };
        foreach (var (name, description) in tools)
        {
            options.ToolCollection.Add(McpServerTool.Create(
                AIFunctionFactory.Create(() => "ok", name, description)));
        }

        if (duplicateToolName is not null)
        {
            options.Handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = [new Tool { Name = duplicateToolName, Description = DescriptionOf(duplicateToolName) }],
            });
        }

        _server = McpServer.Create(serverTransport, options);
        _ = _server.RunAsync();

        ClientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream());
    }

    /// <summary>Gets the description <see cref="InProcessMcpServer"/> gives a tool of this name.</summary>
    /// <param name="toolName">The tool name.</param>
    /// <returns>The description the server reports for it.</returns>
    public static string DescriptionOf(string toolName) => $"The '{toolName}' tool.";

    /// <summary>Starts a server offering one tool that carries no description at all.</summary>
    /// <param name="toolName">The tool's name.</param>
    /// <returns>The server.</returns>
    public static InProcessMcpServer OfferingAToolWithNoDescription(string toolName)
        => new([(toolName, null)]);

    /// <summary>Starts a server whose <c>tools/list</c> response lists one tool name twice.</summary>
    /// <param name="toolName">The name every listed tool shares.</param>
    /// <returns>The server.</returns>
    public static InProcessMcpServer OfferingTheSameToolNameTwice(string toolName)
        => new([(toolName, DescriptionOf(toolName))], toolName);

    /// <summary>Gets the transport a client connects through to reach this server.</summary>
    public IClientTransport ClientTransport { get; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await _server.DisposeAsync().ConfigureAwait(false);
}

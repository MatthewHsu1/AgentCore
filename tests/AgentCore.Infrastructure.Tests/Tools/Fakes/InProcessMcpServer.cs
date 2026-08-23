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
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());

        var options = new McpServerOptions { ToolCollection = new McpServerPrimitiveCollection<McpServerTool>() };
        foreach (var name in toolNames)
        {
            options.ToolCollection.Add(McpServerTool.Create(
                AIFunctionFactory.Create(() => "ok", name, DescriptionOf(name))));
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

    /// <summary>Gets the transport a client connects through to reach this server.</summary>
    public IClientTransport ClientTransport { get; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await _server.DisposeAsync().ConfigureAwait(false);
}

using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using ModelContextProtocol.Client;

namespace AgentCore.Infrastructure.Tools;

/// <summary>
/// Connects to every declared <c>mcp:</c> server, discovers what it offers, and serves the tools
/// <c>allow:</c> pins.
/// </summary>
/// <remarks>
/// The MCP specification's lifecycle section makes shutdown a client obligation, and a stdio server
/// is a child process nothing else can close. This source owns every client it opens, so it must be
/// disposed for the process behind a <c>kind: stdio</c> server to ever be told to stop.
/// </remarks>
public sealed class McpToolSource : IToolSource, IAsyncDisposable
{
    private readonly Func<McpServerConfiguration, IClientTransport> _transports;

    private readonly List<McpClient> _clients = [];

    /// <summary>Creates the source, connecting through the real MCP transports.</summary>
    public McpToolSource()
        : this(DefaultTransport)
    {
    }

    /// <summary>Creates the source over a transport factory a test substitutes.</summary>
    /// <param name="transports">Builds the transport for one declared server.</param>
    internal McpToolSource(Func<McpServerConfiguration, IClientTransport> transports)
    {
        ArgumentNullException.ThrowIfNull(transports);
        _transports = transports;
    }

    /// <inheritdoc />
    /// <exception cref="ConfigurationLoadException">
    /// A server cannot be reached, or an <c>allow:</c> entry names a tool the server does not offer.
    /// Either way the message names the server id, so a deployer knows which <c>mcp:</c> entry is
    /// wrong.
    /// </exception>
    public async ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
        ToolSourceContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        List<ToolRegistration> registrations = [];
        foreach (var server in context.Configuration.Mcp)
        {
            var client = await ConnectAsync(server, cancellationToken).ConfigureAwait(false);
            _clients.Add(client);

            var offered = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var byName = offered.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

            registrations.AddRange(server.Allow.Any(entry => entry.Name == "*")
                ? offered.Select(tool => Register(tool, $"{server.Id}.{tool.Name}"))
                : server.Allow.Select(entry => RegisterAllowed(server, byName, entry)));
        }

        return registrations;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _clients.Clear();
    }

    private async ValueTask<McpClient> ConnectAsync(McpServerConfiguration server, CancellationToken cancellationToken)
    {
        try
        {
            var transport = _transports(server);
            return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ToolSourceError.Fail(
                $"the MCP server '{server.Id}' could not be reached: {ex.Message}");
        }
    }

    private static ToolRegistration RegisterAllowed(
        McpServerConfiguration server, Dictionary<string, McpClientTool> offered, McpAllowEntry entry)
    {
        if (!offered.TryGetValue(entry.Name, out var tool))
        {
            throw ToolSourceError.Fail(
                $"the MCP server '{server.Id}' does not offer a tool named '{entry.Name}', which its "
                + "allow: list names.");
        }

        return Register(tool, entry.As ?? $"{server.Id}.{tool.Name}");
    }

    /// <summary>
    /// Builds the registration for one kept tool, under its final served id.
    /// </summary>
    /// <remarks>
    /// Decision 3's chain for every other kind is a document's own <c>description:</c>, then the
    /// source's default, then a boot failure on empty. An <c>mcp:</c> server has no document
    /// override today — the <c>allow:</c> shape Task 1 built carries no key for one (see Test 7 in
    /// <c>McpToolSourceTests</c>) — so this always takes the server's own description, and an empty
    /// one still fails the boot, in <see cref="ToolRegistryBuilder"/>.
    /// </remarks>
    private static ToolRegistration Register(McpClientTool tool, string id)
    {
        var renamed = tool.WithName(id);
        return new ToolRegistration(id, tool.Description ?? string.Empty, () => renamed);
    }

    private static IClientTransport DefaultTransport(McpServerConfiguration server)
        => server.Transport switch
        {
            McpTransport.Http => new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(server.Url!),
                Name = server.Id,
            }),
            _ => new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Id,
                Command = server.Command[0],
                Arguments = [.. server.Command.Skip(1)],
            }),
        };
}

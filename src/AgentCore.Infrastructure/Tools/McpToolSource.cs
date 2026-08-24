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
    /// A server cannot be reached; an <c>allow:</c> entry names a tool the server does not offer, is
    /// empty, or writes <c>"*"</c> alongside another entry or with an <c>as:</c>; or a kept tool has
    /// no description. Every one of these names the server id, so a deployer knows which
    /// <c>mcp:</c> entry is wrong. On any failure, every client this call already opened is disposed
    /// before the exception leaves — a partly-booted document leaves no child process behind.
    /// </exception>
    public async ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
        ToolSourceContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            List<ToolRegistration> registrations = [];
            foreach (var server in context.Configuration.Mcp)
            {
                registrations.AddRange(await DiscoverAsync(server, cancellationToken).ConfigureAwait(false));
            }

            return registrations;
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
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

    /// <summary>Connects to one server, lists what it offers, and applies its <c>allow:</c>.</summary>
    /// <remarks>
    /// One try covers connecting, <c>tools/list</c>, and the <c>allow:</c> walk: a server that
    /// connects and then fails to list (or offers two tools of one name, which
    /// <see cref="Dictionary{TKey,TValue}"/> itself refuses) must fail the boot by the same route as
    /// one that never connects at all — decision 4 does not stop mattering once a socket opens.
    /// <see cref="ConfigurationLoadException"/> itself passes straight through: it already names the
    /// server and the exact reason, and wrapping it again would only bury that under a second,
    /// vaguer message.
    /// </remarks>
    private async ValueTask<List<ToolRegistration>> DiscoverAsync(
        McpServerConfiguration server, CancellationToken cancellationToken)
    {
        ValidateAllow(server);

        McpClient? client = null;
        try
        {
            var transport = _transports(server);
            client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _clients.Add(client);

            var offered = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var byName = offered.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

            return server.Allow.Any(entry => entry.Name == "*")
                ? [.. offered.Select(tool => Register(server, tool, $"{server.Id}.{tool.Name}"))]
                : [.. server.Allow.Select(entry => RegisterAllowed(server, byName, entry))];
        }
        catch (ConfigurationLoadException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ToolSourceError.Fail(client is null
                ? $"the MCP server '{server.Id}' could not be reached: {Describe(ex)}"
                : $"the MCP server '{server.Id}' connected, but failed before it could list what it "
                    + $"offers: {Describe(ex)}");
        }
    }

    /// <summary>Checks the shape of <c>allow:</c> itself, before any connection is opened.</summary>
    /// <remarks>
    /// Decisions 4 and 8 fail the boot rather than silently doing nothing: an empty <c>allow:</c>
    /// would serve nothing and say nothing. Decision 6 makes <c>"*"</c> the explicit opt-out, and an
    /// opt-out that silently swallows a neighbouring entry — <c>["*", {x: {as: y}}]</c> — or that
    /// carries its own <c>as:</c> is not explicit; it is a wrong document that happened not to fail.
    /// </remarks>
    private static void ValidateAllow(McpServerConfiguration server)
    {
        if (server.Allow.Count == 0)
        {
            throw ToolSourceError.Fail(
                $"the MCP server '{server.Id}' declares no allow:, so it would serve nothing. Pin at "
                + "least one tool, or write allow: [\"*\"] to take every tool it offers.");
        }

        var star = server.Allow.FirstOrDefault(entry => entry.Name == "*");
        if (star is not null && (server.Allow.Count > 1 || star.As is not null))
        {
            throw ToolSourceError.Fail(
                $"the MCP server '{server.Id}' writes \"*\" in allow: alongside another entry, or "
                + "gives \"*\" an as:. \"*\" is the explicit opt-out for every tool the server offers, "
                + "so it must stand alone with no alias.");
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

        return Register(server, tool, entry.As ?? $"{server.Id}.{tool.Name}");
    }

    /// <summary>
    /// Builds the registration for one kept tool, under its final served id.
    /// </summary>
    /// <remarks>
    /// Decision 3's chain for every other kind is a document's own <c>description:</c>, then the
    /// source's default, then a boot failure on empty. A <c>kind: mcp</c> tool has only the second
    /// and third links: the server's own description is the only source there is, MCP makes it
    /// optional — a tool can offer none at all, and then <see cref="McpClientTool.Description"/> is
    /// <c>""</c>, never <see langword="null"/> — and a document has no override today. So this fails
    /// the boot itself on an empty description, before <see cref="ToolRegistryBuilder"/> ever sees
    /// it: that builder's own message tells a deployer to write a <c>description:</c> the
    /// <c>mcp:</c> shape has nowhere to hold, which is advice a deployer could never follow.
    /// </remarks>
    private static ToolRegistration Register(McpServerConfiguration server, McpClientTool tool, string id)
    {
        if (tool.Description.Length == 0)
        {
            throw ToolSourceError.Fail(
                $"the MCP server '{server.Id}' offers a tool '{tool.Name}' with no description, so "
                + "the model has nothing to read when it decides whether to call it. Take it out of "
                + "allow:, or ask whoever runs that server to describe it.");
        }

        var renamed = tool.WithName(id);
        return new ToolRegistration(id, tool.Description, () => renamed);
    }

    /// <summary>Walks to the innermost cause, so a wrapped SDK message never buries the real one.</summary>
    /// <param name="ex">The exception a transport or connection step threw.</param>
    /// <returns>
    /// <paramref name="ex"/>'s own message, plus the deepest <see cref="Exception.InnerException"/>'s
    /// message when one exists and differs — the SDK's own text ("Failed to connect transport.")
    /// names no cause, and the actual one (a missing executable, a refused socket) is what a
    /// deployer needs to fix.
    /// </returns>
    private static string Describe(Exception ex)
    {
        var cause = ex;
        while (cause.InnerException is not null)
        {
            cause = cause.InnerException;
        }

        return ReferenceEquals(cause, ex) ? ex.Message : $"{ex.Message} ({cause.Message})";
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

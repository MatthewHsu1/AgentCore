using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tools.Registry;
using AgentCore.Infrastructure.Tools.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace AgentCore.Infrastructure.Tools;

/// <summary>
/// Connects to every declared <c>mcp:</c> server, discovers what it offers, and serves the tools
/// <c>allow:</c> pins.
/// </summary>
public sealed class McpToolSource : IToolSource, IAsyncDisposable
{
    private readonly Func<McpServerConfiguration, McpServerSession> _sessions;

    private readonly List<McpServerSession> _open = [];

    /// <summary>Creates the source, connecting to the real servers the document declares.</summary>
    /// <param name="secrets">The values startup already resolved, for <c>headers:</c> and <c>env:</c>.</param>
    /// <param name="httpClients">
    /// Opens the client a <c>transport: http</c> server is reached on, or <see langword="null"/> to
    /// let the SDK build its own. See <see cref="McpConnectionFactory"/> for what a host gains by
    /// passing one.
    /// </param>
    /// <param name="loggers">Where reconnects and dropped tools are reported, or <see langword="null"/> for nowhere.</param>
    public McpToolSource(
        ResolvedSecrets secrets,
        Func<HttpClient>? httpClients = null,
        ILoggerFactory? loggers = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        McpConnectionFactory connections = new(secrets, httpClients, loggers);

        _sessions = server => new McpServerSession(
            server,
            (timeout, cancellationToken) => connections.ConnectAsync(server, timeout, cancellationToken),
            loggers);
    }

    /// <summary>Creates the source over a transport a test substitutes.</summary>
    /// <param name="transports">
    /// Builds the transport for one declared server. It is called once per connection attempt, so a
    /// test that expects a reconnect must return a fresh transport each time — one that has already
    /// carried a session cannot carry another.
    /// </param>
    /// <param name="loggers">Where reconnects and dropped tools are reported.</param>
    internal McpToolSource(
        Func<McpServerConfiguration, IClientTransport> transports,
        ILoggerFactory? loggers = null)
        : this(
            (server, timeout, cancellationToken) => Open(transports(server), timeout, loggers, cancellationToken),
            loggers)
    {
        ArgumentNullException.ThrowIfNull(transports);
    }

    /// <summary>Creates the source over a connect step a test substitutes.</summary>
    /// <param name="connect">
    /// Opens one connection to one server, within the timeout given. It is called once per attempt
    /// and again on every reconnect, so it must build a fresh transport each time. A test takes this
    /// form rather than the transport one when it needs to hold the clients themselves — whether one
    /// was closed is only visible on the client.
    /// </param>
    /// <param name="loggers">Where reconnects and dropped tools are reported.</param>
    internal McpToolSource(
        Func<McpServerConfiguration, TimeSpan, CancellationToken, ValueTask<McpClient>> connect,
        ILoggerFactory? loggers = null)
    {
        ArgumentNullException.ThrowIfNull(connect);

        _sessions = server => new McpServerSession(
            server,
            (timeout, cancellationToken) => connect(server, timeout, cancellationToken),
            loggers);
    }

    /// <summary>Opens one client over one transport, under the session's own timeout.</summary>
    private static async ValueTask<McpClient> Open(
        IClientTransport transport, TimeSpan timeout, ILoggerFactory? loggers, CancellationToken cancellationToken)
        => await McpClient.CreateAsync(
            transport,
            new McpClientOptions { InitializationTimeout = timeout },
            loggers,
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    /// <exception cref="ConfigurationLoadException">
    /// A server cannot be reached within its own retry and timeout; an <c>allow:</c> entry names a
    /// tool the server does not offer, is empty, or writes <c>"*"</c> alongside another entry or with
    /// an <c>as:</c>; or a kept tool has no description. Every one of these names the server id, so a
    /// deployer knows which <c>mcp:</c> entry is wrong. On any failure, every session this call
    /// already opened is closed before the exception leaves — a partly-booted document leaves no
    /// child process behind.
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
        // Each dispose is guarded on its own, so one server's failure to close cannot abandon the
        // rest, and _open.Clear() always runs — the host's second call must find nothing left to
        // re-touch.
        foreach (var session in _open)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _open.Clear();
    }

    /// <summary>Opens one server, and applies its <c>allow:</c> to what it offers.</summary>
    private async ValueTask<List<ToolRegistration>> DiscoverAsync(
        McpServerConfiguration server, CancellationToken cancellationToken)
    {
        ValidateAllow(server);

        var session = _sessions(server);

        _open.Add(session);

        var opened = false;

        try
        {
            var offered = await session.OpenAsync(cancellationToken).ConfigureAwait(false);

            opened = true;

            var byName = offered.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

            return server.Allow.Any(entry => entry.Name == "*")
                ? [.. offered.Select(tool => Register(session, server, tool, $"{server.Id}.{tool.Name}"))]
                : [.. server.Allow.Select(entry => RegisterAllowed(session, server, byName, entry))];
        }
        catch (ConfigurationLoadException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ToolSourceError.Fail(opened
                ? $"the MCP server '{server.Id}' listed its tools, but failed before it could serve "
                    + $"them: {Describe(ex)}"
                : $"the MCP server '{server.Id}' could not be reached: {Describe(ex)}");
        }
    }

    /// <summary>Checks the shape of <c>allow:</c> itself, before any connection is opened.</summary>
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
        McpServerSession session,
        McpServerConfiguration server,
        Dictionary<string, McpToolDescriptor> offered,
        McpAllowEntry entry)
    {
        if (!offered.TryGetValue(entry.Name, out var tool))
        {
            throw ToolSourceError.Fail(
                $"the MCP server '{server.Id}' does not offer a tool named '{entry.Name}', which its "
                + "allow: list names.");
        }

        return Register(session, server, tool, entry.As ?? $"{server.Id}.{tool.Name}");
    }

    /// <summary>
    /// Builds the registration for one kept tool, under its final served id.
    /// </summary>
    private static ToolRegistration Register(
        McpServerSession session, McpServerConfiguration server, McpToolDescriptor tool, string id)
    {
        if (tool.Description.Length == 0)
        {
            throw ToolSourceError.Fail(
                $"the MCP server '{server.Id}' offers a tool '{tool.Name}' with no description, so "
                + "the model has nothing to read when it decides whether to call it. Take it out of "
                + "allow: (or, under allow: [\"*\"], replace \"*\" with an explicit list that leaves it "
                + "out), or ask whoever runs that server to describe it.");
        }

        return new ToolRegistration(
            id, tool.Description, () => new McpTool(session, tool, id), session.CallTimeout);
    }

    /// <summary>Names the innermost cause, so a wrapped SDK message never buries the real one.</summary>
    /// <param name="ex">The exception a transport or connection step threw.</param>
    /// <returns>
    /// <paramref name="ex"/>'s own message, plus <see cref="Exception.GetBaseException"/>'s message
    /// when that differs — the SDK's own text ("Failed to connect transport.") names no cause, and
    /// the actual one (a missing executable, a refused socket) is what a deployer needs to fix.
    /// </returns>
    private static string Describe(Exception ex)
    {
        var cause = ex.GetBaseException();
        return ReferenceEquals(cause, ex) ? ex.Message : $"{ex.Message} ({cause.Message})";
    }
}

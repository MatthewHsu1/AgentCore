using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Secrets;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace AgentCore.Infrastructure.Tools.Mcp;

/// <summary>
/// Opens the real connection to one declared MCP server.
/// </summary>
internal sealed class McpConnectionFactory
{
    private readonly ResolvedSecrets _secrets;

    private readonly Func<HttpClient>? _httpClients;

    private readonly ILoggerFactory? _loggers;

    /// <summary>Creates the factory.</summary>
    /// <param name="secrets">The values startup already resolved.</param>
    /// <param name="httpClients">
    /// Opens the client a <c>transport: http</c> server is reached on, or <see langword="null"/> to
    /// let the SDK build its own. A host that passes one keeps its proxy settings, its certificate
    /// configuration, and its logging on MCP traffic too, rather than leaving the transport to build
    /// an <see cref="HttpClient"/> that shares none of them.
    /// </param>
    /// <param name="loggers">Where the SDK writes its own lines, or <see langword="null"/> for nowhere.</param>
    public McpConnectionFactory(
        ResolvedSecrets secrets,
        Func<HttpClient>? httpClients = null,
        ILoggerFactory? loggers = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        _secrets = secrets;
        _httpClients = httpClients;
        _loggers = loggers;
    }

    /// <summary>Opens one connection to one server.</summary>
    /// <param name="server">The declaration.</param>
    /// <param name="timeout">How long the handshake gets.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>The connected client.</returns>
    /// <exception cref="SecretResolutionException">A header or environment value references a name startup did not resolve.</exception>
    public async ValueTask<McpClient> ConnectAsync(
        McpServerConfiguration server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        var transport = Build(server, timeout);
        var options = new McpClientOptions { InitializationTimeout = timeout };

        return await McpClient.CreateAsync(transport, options, _loggers, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves one map of templates into the plain strings a transport takes.</summary>
    private Dictionary<string, string> Resolve(IReadOnlyDictionary<string, SecretTemplate> entries)
    {
        Dictionary<string, string> resolved = new(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            resolved[entry.Key] = _secrets.Format(entry.Value);
        }

        return resolved;
    }

    private IClientTransport Build(McpServerConfiguration server, TimeSpan timeout)
    {
        if (server.Transport is McpTransport.Http)
        {
            var options = new HttpClientTransportOptions
            {
                Endpoint = new Uri(server.Url!),
                Name = server.Id,
                ConnectionTimeout = timeout,
                AdditionalHeaders = Resolve(server.Headers),
            };

            return _httpClients is null
                ? new HttpClientTransport(options, _loggers)

                // ownsClient: false — the pipeline that opened it owns it, and disposing it here
                // would take down a handler chain shared with every other vendor call.
                : new HttpClientTransport(options, _httpClients(), _loggers, false);
        }

        return new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = server.Id,
                Command = server.Command[0],
                Arguments = [.. server.Command.Skip(1)],
                EnvironmentVariables = Resolve(server.Env)
                    .ToDictionary(entry => entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal),
                InheritEnvironmentVariables = server.InheritEnv,
            },
            _loggers);
    }
}

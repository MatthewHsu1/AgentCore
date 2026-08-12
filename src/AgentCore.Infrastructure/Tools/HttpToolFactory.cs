using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;

namespace AgentCore.Infrastructure.Tools;

/// <summary>
/// Builds the <c>kind: http</c> tools.
/// </summary>
/// <remarks>
/// <para>
/// The factory holds the one <see cref="HttpClient"/> the host gives it, and never builds one. The
/// host owns the handler, the pool, and the timeout, and a test owns the whole endpoint by handing
/// over a client on its own <see cref="HttpMessageHandler"/>.
/// </para>
/// <para>
/// Each header resolves once, here, when the document compiles. A tool that carried an unresolved
/// <c>${secret:name}</c> into a call would fail on the telephone instead of at startup.
/// </para>
/// </remarks>
public sealed class HttpToolFactory : IAgentToolFactory
{
    private readonly HttpClient _client;
    private readonly ResolvedSecrets _secrets;

    /// <summary>Creates the factory.</summary>
    /// <param name="client">The client every tool of this factory sends on. The factory never disposes it.</param>
    /// <param name="secrets">The values startup already resolved.</param>
    public HttpToolFactory(HttpClient client, ResolvedSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(secrets);

        _client = client;
        _secrets = secrets;
    }

    /// <summary>Builds one HTTP tool.</summary>
    /// <param name="tool">The declared tool.</param>
    /// <returns>The tool, or <see langword="null"/> when the kind is not <see cref="ToolKind.Http"/>.</returns>
    /// <exception cref="ConfigurationLoadException">The tool declares no <c>request:</c>.</exception>
    /// <exception cref="SecretResolutionException">A header references a secret startup did not resolve.</exception>
    public AITool? Create(ToolConfiguration tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (tool.Kind != ToolKind.Http)
        {
            return null;
        }

        if (tool.Request is not { } request)
        {
            throw new ConfigurationLoadException(new ConfigurationError
            {
                Pointer = "/tools",
                Message = $"the tool '{tool.Id}' is kind: http and declares no request:, so there is no call to make.",
                Check = ConfigurationCheck.ReferenceResolution,
            });
        }

        List<KeyValuePair<string, string>> headers = [];
        foreach (var header in request.Headers)
        {
            headers.Add(new KeyValuePair<string, string>(header.Key, _secrets.Format(header.Value)));
        }

        return new HttpTool(tool, request, headers, _client);
    }
}

/// <summary>One <c>kind: http</c> tool: one request, one answer.</summary>
internal sealed class HttpTool : DeclaredTool
{
    /// <summary>The number of characters of a failing body the error result quotes.</summary>
    private const int BodyQuoteLimit = 500;

    private readonly HttpRequestConfiguration _request;
    private readonly List<KeyValuePair<string, string>> _headers;
    private readonly HttpClient _client;

    internal HttpTool(
        ToolConfiguration tool,
        HttpRequestConfiguration request,
        List<KeyValuePair<string, string>> headers,
        HttpClient client)
        : base(tool)
    {
        _request = request;
        _headers = headers;
        _client = client;
    }

    protected override async ValueTask<object?> CallAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (!TryFillUrl(arguments, out var url, out var missing))
        {
            return Failed($"the call filled no '{missing}', and the URL needs it.");
        }

        using HttpRequestMessage message = new(new HttpMethod(_request.Method), url);
        foreach (var header in _headers)
        {
            // TryAddWithoutValidation: a document writes the header the endpoint asks for, and the
            // client never rewrites it.
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var response = await _client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The status and the body, and never a request header: the header holds the credential.
            return Failed(
                $"the endpoint answered {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)} "
                + $"{response.StatusCode}. {Quote(body)}");
        }

        // A tool result has no declared shape, and the state layer reads a path into it, so a JSON
        // body stays a node tree. Anything else comes back as the text the endpoint wrote.
        return TryParse(body, out var node) ? node : body;
    }

    /// <summary>Fills every <c>{name}</c> placeholder of the URL from the arguments.</summary>
    private bool TryFillUrl(AIFunctionArguments arguments, out string url, out string missing)
    {
        url = _request.Url;
        missing = string.Empty;

        var open = url.IndexOf('{', StringComparison.Ordinal);
        while (open >= 0)
        {
            var close = url.IndexOf('}', open + 1);
            if (close < 0)
            {
                break;
            }

            var name = url[(open + 1)..close];
            if (ArgumentText(arguments, name) is not { Length: > 0 } value)
            {
                missing = name;
                return false;
            }

            var replacement = Uri.EscapeDataString(value);
            url = url[..open] + replacement + url[(close + 1)..];
            open = url.IndexOf('{', open + replacement.Length);
        }

        return true;
    }

    /// <summary>Reads a body as JSON, and reports whether it is JSON at all.</summary>
    private static bool TryParse(string body, out JsonNode? node)
    {
        node = null;
        if (body.Length == 0)
        {
            return false;
        }

        try
        {
            node = JsonNode.Parse(body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Cuts a failing body down to the part the model reads.</summary>
    private static string Quote(string body)
        => body.Length <= BodyQuoteLimit ? body : body[..BodyQuoteLimit] + "...";
}

using AgentCore.TestSupport;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Fakes;

/// <summary>
/// One real host, on a real socket, over a fake model.
/// </summary>
/// <remarks>
/// Kestrel takes port zero and reports the port it got, so many tests run at once. The socket is real
/// on purpose: a server-sent event is a wire behaviour, and an in-memory pipe would not prove that the
/// endpoint writes one event for each update. No test here reaches a network or needs an API key.
/// </remarks>
internal sealed class ChatCompletionsHost : IAsyncDisposable
{
    private const string DataPrefix = "data: ";

    private readonly WebApplication _app;

    private ChatCompletionsHost(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    /// <summary>Gets the client that speaks to the host.</summary>
    public HttpClient Client { get; }

    /// <summary>Starts one host over one document.</summary>
    /// <param name="yaml">The document, as YAML.</param>
    /// <param name="reply">The model behind every name the routing factory does not hold.</param>
    /// <param name="fill">The model behind the <c>fill</c> name, or null.</param>
    /// <param name="configure">Anything else the test binds on the options.</param>
    /// <returns>The started host.</returns>
    public static async Task<ChatCompletionsHost> StartAsync(
        string yaml,
        IChatClient reply,
        IChatClient? fill = null,
        Action<AgentCoreOptions>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddAgentCore(options =>
        {
            options.Configuration = ConfigurationLoader.LoadYaml(yaml);
            options.UseChatClients(_ =>
            {
                RoutingChatClientFactory factory = new(reply);
                if (fill is not null)
                {
                    factory.Route("fill", fill);
                }

                return factory;
            });

            configure?.Invoke(options);
        });

        var app = builder.Build();
        app.MapChatCompletions();
        await app.StartAsync();

        var address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .First();

        HttpClient client = new() { BaseAddress = new Uri(address, UriKind.Absolute) };
        return new ChatCompletionsHost(app, client);
    }

    /// <summary>Sends one turn and reads the whole answer.</summary>
    /// <param name="text">What the caller says.</param>
    /// <param name="session">The id of an open call, or null to start one.</param>
    /// <returns>The answer.</returns>
    public Task<HttpResponseMessage> PostAsync(string text, string? session = null)
        => SendAsync(text, session, stream: false, HttpCompletionOption.ResponseContentRead);

    /// <summary>Sends one turn and reads the answer as it arrives.</summary>
    /// <param name="text">What the caller says.</param>
    /// <param name="session">The id of an open call, or null to start one.</param>
    /// <returns>The answer, with the body still open.</returns>
    public Task<HttpResponseMessage> PostStreamingAsync(string text, string? session = null)
        => SendAsync(text, session, stream: true, HttpCompletionOption.ResponseHeadersRead);

    /// <summary>Reads every server-sent event of one answer.</summary>
    /// <param name="response">The answer, with the body still open.</param>
    /// <returns>The text after each <c>data:</c> prefix, in order.</returns>
    public static async Task<List<string>> ReadEventsAsync(HttpResponseMessage response)
    {
        List<string> events = [];
        await using var body = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using StreamReader reader = new(body, Encoding.UTF8);

        while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { } line)
        {
            if (line.StartsWith(DataPrefix, StringComparison.Ordinal))
            {
                events.Add(line[DataPrefix.Length..]);
            }
        }

        return events;
    }

    /// <summary>Reads one answer as a node tree.</summary>
    /// <param name="response">The answer.</param>
    /// <returns>The body.</returns>
    public static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonNode.Parse(text)!;
    }

    /// <summary>Reads the text of every content delta of one stream.</summary>
    /// <param name="events">The events the stream carried.</param>
    /// <returns>The pieces, in order.</returns>
    public static List<string> ContentDeltas(IEnumerable<string> events)
    {
        List<string> pieces = [];
        foreach (var chunk in Chunks(events))
        {
            if (chunk["choices"]?[0]?["delta"]?["content"]?.GetValue<string>() is { Length: > 0 } text)
            {
                pieces.Add(text);
            }
        }

        return pieces;
    }

    /// <summary>Reads the AgentCore extension object of the last chunk that carried one.</summary>
    /// <param name="events">The events the stream carried.</param>
    /// <returns>The object, or null when no chunk carried one.</returns>
    public static JsonNode? LastTurnInfo(IEnumerable<string> events)
    {
        JsonNode? found = null;
        foreach (var chunk in Chunks(events))
        {
            if (chunk["agentcore"] is { } info)
            {
                found = info;
            }
        }

        return found;
    }

    /// <summary>Stops the host and releases the socket.</summary>
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>Reads every event that carries a chunk, and skips the closing marker.</summary>
    private static IEnumerable<JsonNode> Chunks(IEnumerable<string> events)
    {
        foreach (var raw in events)
        {
            if (!string.Equals(raw, "[DONE]", StringComparison.Ordinal))
            {
                yield return JsonNode.Parse(raw)!;
            }
        }
    }

    private Task<HttpResponseMessage> SendAsync(
        string text,
        string? session,
        bool stream,
        HttpCompletionOption completion)
    {
        HttpRequestMessage request = new(HttpMethod.Post, ChatCompletionsEndpointRouteBuilderExtensions.DefaultPattern)
        {
            Content = JsonContent.Create(
                new
                {
                    model = "service-voice",
                    messages = new[] { new { role = "user", content = text } },
                    stream,
                },
                options: JsonSerializerOptions.Web),
        };

        if (session is { Length: > 0 })
        {
            request.Headers.Add(ChatCompletionsEndpointRouteBuilderExtensions.SessionHeaderName, session);
        }

        return Client.SendAsync(request, completion, TestContext.Current.CancellationToken);
    }
}

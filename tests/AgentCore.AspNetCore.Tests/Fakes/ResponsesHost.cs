using AgentCore.Application.Configuration.Parsing;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.TestSupport;
using System.Text;
using System.Text.Json.Nodes;
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
/// One real host, on a real socket, over a fake model, answering the Responses path.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ChatCompletionsHost"/>: Kestrel takes port zero and reports the
/// port it got, so many tests run at once. No test here reaches a network or needs
/// an API key.
/// </remarks>
internal sealed class ResponsesHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ResponsesHost(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    /// <summary>Gets the client that speaks to the host.</summary>
    public HttpClient Client { get; }

    /// <summary>Starts one host over one document.</summary>
    /// <param name="yaml">The document, as YAML.</param>
    /// <param name="reply">The model behind every name the routing factory does not hold.</param>
    /// <param name="configure">Anything else the test binds on the options.</param>
    /// <returns>The started host.</returns>
    public static async Task<ResponsesHost> StartAsync(
        string yaml, IChatClient reply, Action<AgentCoreOptions>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddAgentCore(options =>
        {
            options.Configuration = ConfigurationLoader.LoadYaml(yaml);
            options.UseChatClients(_ => new RoutingChatClientFactory(reply));
            configure?.Invoke(options);
        });

        var app = builder.Build();
        app.MapResponses();
        await app.StartAsync();

        var address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .First();

        HttpClient client = new() { BaseAddress = new Uri(address, UriKind.Absolute) };
        return new ResponsesHost(app, client);
    }

    /// <summary>Sends one Responses request body.</summary>
    /// <param name="json">The request body.</param>
    /// <returns>The answer.</returns>
    public async Task<HttpResponseMessage> PostAsync(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await Client.PostAsync(
            ResponsesEndpointRouteBuilderExtensions.DefaultPattern, content, TestContext.Current.CancellationToken);
    }

    /// <summary>Reads one answer as a node tree.</summary>
    /// <param name="response">The answer.</param>
    /// <returns>The body.</returns>
    public static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonNode.Parse(text)!;
    }

    /// <summary>Reads one answer as text.</summary>
    /// <param name="response">The answer.</param>
    /// <returns>The body.</returns>
    public static Task<string> ReadTextAsync(HttpResponseMessage response)
        => response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

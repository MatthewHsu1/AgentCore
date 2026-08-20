using System.Net;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.Chat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Chat.Tests;

/// <summary>
/// What the chat route answers.
/// </summary>
/// <remarks>
/// <para>
/// Two things here are easy to break and impossible to notice from a passing build. The first is
/// that an asset has to win over the deep-link fallback: the usual pairing of
/// <c>UseStaticFiles</c> with a fallback route serves the page in place of every script the page
/// asks for, because the static file middleware stands down once routing has selected an endpoint.
/// The page still loads, blank, with a 200 for everything. The second is the endpoint rewrite: a
/// page pointed at the wrong route posts into a 404 and the failure surfaces nowhere near here.
/// </para>
/// <para>
/// These tests need no AgentCore services at all. <c>MapAgentCoreChat</c> serves files and rewrites
/// one attribute; the turn loop it points the browser at is somebody else's route.
/// </para>
/// </remarks>
public sealed class AgentCoreChatTests
{
    [Fact]
    public async Task ThePageIsServedOnTheUiPath()
    {
        await using var app = await StartAsync();
        using var client = ClientFor(app);

        var response = await client.GetAsync(AgentCoreChatExtensions.UiPath, TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<div id=\"root\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheScriptThePageNamesIsServedAsAScriptAndNotAsThePage()
    {
        // The regression this catches is silent: every asset answers 200 with the page's own HTML,
        // so the browser loads a blank screen and the network tab shows nothing wrong.
        await using var app = await StartAsync();
        using var client = ClientFor(app);

        var script = ScriptOf(await client.GetStringAsync(
            AgentCoreChatExtensions.UiPath,
            TestContext.Current.CancellationToken));

        var response = await client.GetAsync(script, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.True(
            response.Content.Headers.ContentLength > 1000,
            "the bundle is larger than the page, so a page-sized answer is the page.");
    }

    [Fact]
    public async Task ADeepLinkFallsBackToThePage()
    {
        // The UI owns its own routing, so a reload on any path under /chat has to reach the page.
        await using var app = await StartAsync();
        using var client = ClientFor(app);

        var response = await client.GetAsync(
            $"{AgentCoreChatExtensions.UiPath}/some/thread",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "<div id=\"root\">",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePageIsPointedAtTheRouteTheHostMapped()
    {
        const string Moved = "/agentcore/v1/chat/completions";
        await using var app = await StartAsync(Moved);
        using var client = ClientFor(app);

        var html = await client.GetStringAsync(AgentCoreChatExtensions.UiPath, TestContext.Current.CancellationToken);

        Assert.Contains($"data-agentcore-endpoint=\"{Moved}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"data-agentcore-endpoint=\"{ChatCompletionsEndpointRouteBuilderExtensions.DefaultPattern}\"",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatNamesNoRouteGetsTheDefaultOne()
    {
        await using var app = await StartAsync();
        using var client = ClientFor(app);

        var html = await client.GetStringAsync(AgentCoreChatExtensions.UiPath, TestContext.Current.CancellationToken);

        Assert.Contains(
            $"data-agentcore-endpoint=\"{ChatCompletionsEndpointRouteBuilderExtensions.DefaultPattern}\"",
            html,
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Starts one host with the chat route mapped and nothing else.</summary>
    /// <param name="chatCompletionsPattern">The route the page should post to, or null for the default.</param>
    /// <returns>The started host.</returns>
    private static async Task<WebApplication> StartAsync(string? chatCompletionsPattern = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapAgentCoreChat(chatCompletionsPattern);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    /// <summary>Builds a client for the address Kestrel took, since the tests ask for port zero.</summary>
    /// <param name="app">The started host.</param>
    /// <returns>The client.</returns>
    private static HttpClient ClientFor(WebApplication app)
        => new()
        {
            BaseAddress = new Uri(
                app.Services
                    .GetRequiredService<IServer>()
                    .Features
                    .Get<IServerAddressesFeature>()!
                    .Addresses
                    .First(),
                UriKind.Absolute),
        };

    /// <summary>Reads the bundle URL out of the page.</summary>
    /// <param name="html">The page.</param>
    /// <returns>The path the page's module script is at.</returns>
    /// <remarks>
    /// The bundler writes a content hash into the name, so it changes on every build of the UI and
    /// cannot be written down here.
    /// </remarks>
    private static string ScriptOf(string html)
    {
        const string Marker = "src=\"";
        var start = html.IndexOf("<script type=\"module\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the page carries no module script, so the UI was not built.");

        var from = html.IndexOf(Marker, start, StringComparison.Ordinal) + Marker.Length;
        var to = html.IndexOf('"', from);
        return html[from..to];
    }
}

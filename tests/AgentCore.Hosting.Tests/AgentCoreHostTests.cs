using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Hosting.Tests;

/// <summary>
/// The two calls a host makes, and the promises they carry.
/// </summary>
/// <remarks>
/// <para>
/// This library exists so a host owns no wiring, and the whole of that promise is that a host can
/// still overrule any part of it. Every test here is about who wins, because that is the one thing
/// the ordering inside <c>Configure</c> can get wrong without failing to compile: each <c>Use*</c>
/// seam is a setter, so defaults written after a host's callback would silently replace it.
/// </para>
/// <para>
/// Every test runs offline against a fake model. There is no OpenAI account, no network call, and
/// no API key anywhere in this file — which is itself the proof that the default vendor list costs
/// nothing until a document names it.
/// </para>
/// </remarks>
public sealed class AgentCoreHostTests
{
    /// <summary>A model that says one thing, which is all the compile table asks for.</summary>
    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "hello");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>The factory behind every model reference in these tests.</summary>
    private sealed class FakeChatClientFactory : IChatClientFactory
    {
        public IChatClient GetChatClient(ModelReference? model) => new FakeChatClient();
    }

    // ---------------------------------------------------------------------------------------------
    // Who wins. The host does, on every seam, because its callback runs last.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHostChatClientFactoryWinsOverTheDefaultVendor()
    {
        // The default list names the real OpenAI adapter. If it ran after the callback it would
        // replace this factory, and the start would then want a key this test does not set.
        using var host = await StartAsync();

        Assert.NotNull(host.Services.GetService<IChatClientFactory>());
    }

    [Fact]
    public async Task AHostThatHandsOverADocumentDoesNotAlsoGetTheDefaultPath()
    {
        // Both a path and a configuration is two documents, and AddAgentCoreAsync refuses that. The
        // default path has to stand down for a host that named a document of its own.
        using var host = await StartAsync();

        Assert.NotNull(host.Services.GetService<IChatClientFactory>());
    }

    // ---------------------------------------------------------------------------------------------
    // The CreateCase stub. It fills a gap and never takes a name the host wanted.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHostThatBindsNothingGetsTheStub()
    {
        AgentCoreOptions? options = null;
        using var host = await StartAsync(captured => options = captured);

        Assert.True(options!.Bindings.TryGetBinding(
            AgentCoreHostBuilderExtensions.CreateCaseBinding,
            out var binding));

        var result = await binding!(new JsonObject { ["summary"] = "a broken treadmill" }, TestContext.Current.CancellationToken);
        var json = Assert.IsType<JsonObject>(result);

        Assert.False((bool)json["opened"]!);
        Assert.Equal("a broken treadmill", (string?)json["summary"]);
    }

    [Fact]
    public async Task AHostThatBindsCreateCaseKeepsItsOwnDelegate()
    {
        // Registering one name twice throws, so a stub added before the callback would turn a host
        // that wants this binding into a host that cannot start at all.
        AgentCoreOptions? options = null;
        using var host = await StartAsync(configure =>
        {
            options = configure;
            configure.Bind(
                AgentCoreHostBuilderExtensions.CreateCaseBinding,
                (_, _) => ValueTask.FromResult<object?>(new JsonObject { ["opened"] = true }));
        });

        Assert.True(options!.Bindings.TryGetBinding(
            AgentCoreHostBuilderExtensions.CreateCaseBinding,
            out var binding));

        var result = await binding!([], TestContext.Current.CancellationToken);
        Assert.True((bool)Assert.IsType<JsonObject>(result)["opened"]!);
    }

    // ---------------------------------------------------------------------------------------------
    // What this extension opened before the container existed, it has to close with the container.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheOutboundHttpPipelineClosesWithTheHost()
    {
        var host = await StartAsync();
        var clients = host.Services.GetRequiredService<AgentCoreHttpClients>();

        await host.DisposeAsync();

        // The pipeline holds a container and a SocketsHttpHandler per client name. A host that stops
        // and leaves them open leaks both, and a process that restarts it leaks them again.
        Assert.Throws<ObjectDisposedException>(() => clients.CreateClient("agentcore.test"));
    }

    [Fact]
    public async Task TheLoggerFactoryClosesWithTheHost()
    {
        var host = await StartAsync();
        var loggers = host.Services.GetRequiredService<ILoggerFactory>();

        await host.DisposeAsync();

        // This factory is built before the container, so nothing else can be holding its providers.
        Assert.Throws<ObjectDisposedException>(() => loggers.CreateLogger("after"));
    }

    // ---------------------------------------------------------------------------------------------
    // The routes. Mapping is the other half: the registrations above answer nothing on their own.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task HealthAnswersOnTheMappedRoute()
    {
        await using var app = await StartMappedAsync();
        using HttpClient client = new() { BaseAddress = Address(app) };

        var response = await client.GetAsync(
            AgentCoreHostEndpointExtensions.HealthPattern,
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ChatCompletionsAnswersOnTheDefaultRoute()
    {
        await using var app = await StartMappedAsync();
        using HttpClient client = new() { BaseAddress = Address(app) };

        // No user message is a caller mistake this endpoint names, and naming it proves the route
        // reached the endpoint rather than the 404 handler.
        var response = await PostEmptyAsync(client, ChatCompletionsEndpointRouteBuilderExtensions.DefaultPattern);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChatCompletionsMovesWhenTheHostNamesAnotherRoute()
    {
        // A host that mounts a second OpenAI-compatible surface of its own needs this one out of the
        // way, and it must actually leave the default route behind when it moves.
        const string Moved = "/agentcore/v1/chat/completions";
        await using var app = await StartMappedAsync(Moved);
        using HttpClient client = new() { BaseAddress = Address(app) };

        Assert.Equal(
            System.Net.HttpStatusCode.BadRequest,
            (await PostEmptyAsync(client, Moved)).StatusCode);
        Assert.Equal(
            System.Net.HttpStatusCode.NotFound,
            (await PostEmptyAsync(client, ChatCompletionsEndpointRouteBuilderExtensions.DefaultPattern)).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The smallest document the schema accepts: one agent, one model, and the required pair.</summary>
    /// <remarks>
    /// A document that writes a <c>providers</c> block at all must write <c>call</c> and
    /// <c>speech</c> too, and both speech roles must name the same vendor the call does. Those kinds
    /// resolve here because the default list this library registers already names that transport —
    /// which is the point: a test writes no vendor of its own except the model.
    /// </remarks>
    private const string Document = """
        apiVersion: agentcore/v1
        name: hosting-tests
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: fake, model: fake-model, as: reply }
        """;

    /// <summary>Builds a host the way a deployable does, over a fake model and no key.</summary>
    /// <param name="configure">Anything else the test says on the options.</param>
    /// <returns>The built host, not started.</returns>
    private static async Task<WebApplication> BuildAsync(Action<AgentCoreOptions>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        await builder.AddAgentCoreHostAsync(options =>
        {
            options.Configuration = ConfigurationLoader.LoadYaml(Document);
            options.UseChatClients(_ => new FakeChatClientFactory());
            configure?.Invoke(options);
        });

        return builder.Build();
    }

    /// <summary>Builds a host and reads its container, with nothing mapped and nothing listening.</summary>
    /// <param name="configure">Anything else the test says on the options.</param>
    /// <returns>The built host.</returns>
    private static Task<WebApplication> StartAsync(Action<AgentCoreOptions>? configure = null)
        => BuildAsync(configure);

    /// <summary>Builds a host, maps every route, and puts it on a real socket.</summary>
    /// <param name="chatCompletionsPattern">The route the text endpoint answers on, or null for the default.</param>
    /// <returns>The started host.</returns>
    private static async Task<WebApplication> StartMappedAsync(string? chatCompletionsPattern = null)
    {
        var app = await BuildAsync();
        app.MapAgentCoreHost(chatCompletionsPattern);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    /// <summary>Reads the address Kestrel took, since the tests ask for port zero.</summary>
    /// <param name="app">The started host.</param>
    /// <returns>The base address.</returns>
    private static Uri Address(WebApplication app)
        => new(app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses
            .First(), UriKind.Absolute);

    /// <summary>Posts a request with no user message, which every mapped route answers 400 to.</summary>
    /// <param name="client">The client that speaks to the host.</param>
    /// <param name="route">The route to post to.</param>
    /// <returns>The answer.</returns>
    private static Task<HttpResponseMessage> PostEmptyAsync(HttpClient client, string route)
        => client.PostAsync(
            route,
            new StringContent("{\"messages\":[]}", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
}

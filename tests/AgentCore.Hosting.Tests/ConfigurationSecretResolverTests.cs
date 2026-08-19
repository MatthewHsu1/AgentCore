using AgentCore.Application.Ports;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Hosting.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Hosting.Tests;

/// <summary>
/// The third link of the secret chain: whatever the host's own configuration holds.
/// </summary>
/// <remarks>
/// <para>
/// The environment and the <c>/run/secrets</c> directory are how a deployment hands a credential
/// over. Neither helps a developer running <c>dotnet run</c>, who has <c>dotnet user-secrets</c> and
/// an <c>appsettings.Development.json</c> already. This link reaches both, because both are
/// configuration providers and nothing else about them is this library's business.
/// </para>
/// <para>
/// It reads ONE section and never the whole document. A bare key at the root would collide with
/// unrelated host settings, and the environment provider that <c>WebApplicationBuilder</c> already
/// installs would make this link answer for variables the first link owns.
/// </para>
/// <para>Every test here runs offline. There is no network call and no API key in this file.</para>
/// </remarks>
public sealed class ConfigurationSecretResolverTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheConfigurationResolverReadsTheNameTheDocumentWrote()
    {
        var resolver = Resolver(("AgentCore:Secrets:orders-api-key", "sk-1"));

        Assert.Equal("sk-1", await resolver.TryResolveAsync("orders-api-key", Token));
    }

    [Fact]
    public async Task TheConfigurationResolverAlsoReadsTheShoutingForm()
    {
        // The same courtesy the environment link gives: a deployment that already writes
        // ORDERS_API_KEY keeps the spelling it has.
        var resolver = Resolver(("AgentCore:Secrets:ORDERS_API_KEY", "sk-2"));

        Assert.Equal("sk-2", await resolver.TryResolveAsync("orders-api-key", Token));
    }

    [Fact]
    public async Task TheConfigurationResolverAnswersNullForAnUnknownName()
    {
        var resolver = Resolver(("AgentCore:Secrets:something-else", "sk-3"));

        Assert.Null(await resolver.TryResolveAsync("orders-api-key", Token));
    }

    [Fact]
    public async Task TheConfigurationResolverReadsAnEmptyValueAsUnset()
    {
        // A miss, so the chain goes on rather than handing an adapter an empty credential.
        var resolver = Resolver(("AgentCore:Secrets:orders-api-key", ""));

        Assert.Null(await resolver.TryResolveAsync("orders-api-key", Token));
    }

    [Fact]
    public async Task TheConfigurationResolverIgnoresAKeyOutsideItsSection()
    {
        // A root key is host settings, not a credential store. Reading it would let any unrelated
        // setting answer for a secret name that happened to match.
        var resolver = Resolver(("orders-api-key", "sk-4"), ("Secrets:orders-api-key", "sk-5"));

        Assert.Null(await resolver.TryResolveAsync("orders-api-key", Token));
    }

    [Fact]
    public async Task AHostResolvesASecretFromItsOwnConfiguration()
    {
        // The whole point: dotnet user-secrets and appsettings.Development.json both land here.
        AgentCoreOptions? options = null;
        using var host = await BuildAsync(
            captured => options = captured,
            ("AgentCore:Secrets:orders-api-key", "sk-from-config"));

        Assert.NotNull(options!.SecretResolver);
        Assert.Equal("sk-from-config", await options.SecretResolver.TryResolveAsync("orders-api-key", Token));
    }

    [Fact]
    public async Task AnEnvironmentVariableWinsOverTheSameNameInConfiguration()
    {
        // A deployment's own variable must beat anything a committed settings file carries.
        const string Name = "agentcore-hosting-tests-precedence-key";
        Environment.SetEnvironmentVariable(Name, "sk-from-environment");
        try
        {
            AgentCoreOptions? options = null;
            using var host = await BuildAsync(
                captured => options = captured,
                ($"AgentCore:Secrets:{Name}", "sk-from-config"));

            Assert.Equal("sk-from-environment", await options!.SecretResolver!.TryResolveAsync(Name, Token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, null);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------
    private const string Document = """
        apiVersion: agentcore/v1
        name: secret-chain-tests
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

    private static ConfigurationSecretResolver Resolver(params (string Key, string Value)[] entries)
        => new ConfigurationSecretResolver(
            new ConfigurationBuilder()
                .AddInMemoryCollection(entries.Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value)))
                .Build());

    private static async Task<WebApplication> BuildAsync(
        Action<AgentCoreOptions> configure,
        params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(
            settings.Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value)));

        await builder.AddAgentCoreHostAsync(options =>
        {
            options.Configuration = ConfigurationLoader.LoadYaml(Document);
            options.UseChatClients(_ => new FakeSecretChainChatClientFactory());
            configure(options);
        });

        return builder.Build();
    }

    private sealed class FakeSecretChainChatClientFactory : IChatClientFactory
    {
        public IChatClient GetChatClient(ModelReference? model) => new FakeSecretChainChatClient();
    }

    private sealed class FakeSecretChainChatClient : IChatClient
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
}

using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Llm;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Infrastructure.Llm.OpenAI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Llm.OpenAI;

/// <summary>
/// The OpenAI adapter. It owns the vendor only: the SDK client, the key, the model name.
/// </summary>
/// <remarks>
/// Building a client opens no socket, so every test here runs offline. The key is a fake string, and
/// no test sends a request.
/// </remarks>
public sealed class OpenAiChatClientAdapterTests
{
    private const string FakeKey = "sk-test-not-a-real-key";

    private const string TwoModelsYaml =
        """
        apiVersion: agentcore/v1
        name: two-models
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
            - { kind: openai, model: gpt-5.4-nano, as: fill }
        """;

    [Fact]
    public void TheAdapter_ServesTheOpenAiKind()
    {
        Assert.Equal("openai", new OpenAiChatClientAdapter().Kind);
    }

    [Fact]
    public async Task TheApiKey_ComesFromTheResolverChain()
    {
        var entry = FirstEntry();
        MapSecretResolver resolver = new();
        resolver.With(OpenAiChatClientAdapter.ApiKeySecretName, FakeKey);

        var client = await new OpenAiChatClientAdapter().CreateClientAsync(
            entry,
            resolver,
            TestContext.Current.CancellationToken);

        Assert.NotNull(client);
    }

    [Fact]
    public async Task TheApiKey_ResolvesOnceHoweverManyEntriesTheDocumentDeclares()
    {
        CountingSecretResolver resolver = new();
        OpenAiChatClientAdapter adapter = new();
        var llm = Document().Providers!.Llm;

        await adapter.CreateClientAsync(llm[0], resolver, TestContext.Current.CancellationToken);
        await adapter.CreateClientAsync(llm[1], resolver, TestContext.Current.CancellationToken);

        // One vendor client for the whole document, so one key read and one connection pool.
        Assert.Equal(1, resolver.Resolutions);
    }

    [Fact]
    public async Task NoApiKeyAnywhere_FailsAndSaysWhereToPutOne()
    {
        var saved = Environment.GetEnvironmentVariable(OpenAiChatClientAdapter.ApiKeyVariableName);
        Environment.SetEnvironmentVariable(OpenAiChatClientAdapter.ApiKeyVariableName, null);

        try
        {
            var failure = await Assert.ThrowsAsync<SecretResolutionException>(
                async () => await new OpenAiChatClientAdapter().CreateClientAsync(
                    FirstEntry(),
                    new MapSecretResolver(),
                    TestContext.Current.CancellationToken));

            Assert.Contains(OpenAiChatClientAdapter.ApiKeySecretName, failure.Message, StringComparison.Ordinal);
            Assert.Contains(OpenAiChatClientAdapter.ApiKeyVariableName, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpenAiChatClientAdapter.ApiKeyVariableName, saved);
        }
    }

    [Fact]
    public async Task TheComposite_ServesAnOpenAiDocumentThroughThisAdapter()
    {
        MapSecretResolver resolver = new();
        resolver.With(OpenAiChatClientAdapter.ApiKeySecretName, FakeKey);

        using var factory = await CompositeChatClientFactory.CreateAsync(
            Document(),
            resolver,
            [new OpenAiChatClientAdapter()],
            TestContext.Current.CancellationToken);

        var reply = factory.GetChatClient(new ModelReference { Ref = "reply" });

        Assert.Same(reply, factory.GetChatClient(new ModelReference { Ref = "reply" }));
        Assert.NotSame(reply, factory.GetChatClient(new ModelReference { Ref = "fill" }));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------
    private static AgentCoreConfiguration Document() => ConfigurationLoader.LoadYaml(TwoModelsYaml);

    private static LlmProviderConfiguration FirstEntry() => Document().Providers!.Llm[0];

    /// <summary>A resolver that counts its reads, so a test proves the key resolves once.</summary>
    private sealed class CountingSecretResolver : ISecretResolverPort
    {
        public int Resolutions { get; private set; }

        public ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
        {
            Resolutions++;
            return ValueTask.FromResult<string?>(FakeKey);
        }
    }
}

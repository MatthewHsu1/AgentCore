using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Secrets;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Infrastructure.Llm;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Llm;

/// <summary>
/// The OpenAI adapter. It holds the whole mapping from an <c>as</c> name to a client.
/// </summary>
/// <remarks>
/// Building a client opens no socket, so every test here runs offline. The key is a fake string, and
/// no test sends a request.
/// </remarks>
public sealed class OpenAiChatClientFactoryTests
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

    private const string OtherVendorYaml =
        """
        apiVersion: agentcore/v1
        name: other-vendor
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          llm:
            - { kind: anthropic, model: claude-4, as: reply }
        """;

    [Fact]
    public void TheFactory_BuildsOneClientForEachAsNameAndSharesIt()
    {
        using OpenAiChatClientFactory factory = new(ConfigurationLoader.LoadYaml(TwoModelsYaml), FakeKey);

        var reply = factory.GetChatClient(new ModelReference { Ref = "reply" });
        var fill = factory.GetChatClient(new ModelReference { Ref = "fill" });

        Assert.Same(reply, factory.GetChatClient(new ModelReference { Ref = "reply" }));
        Assert.NotSame(reply, fill);
    }

    [Fact]
    public void AReferenceWithNoModel_TakesTheFirstDeclaredEntry()
    {
        using OpenAiChatClientFactory factory = new(ConfigurationLoader.LoadYaml(TwoModelsYaml), FakeKey);

        Assert.Same(factory.GetChatClient(new ModelReference { Ref = "reply" }), factory.GetChatClient(null));
    }

    [Fact]
    public void AnUnknownAsName_FailsAndNamesWhatTheDocumentDoesDeclare()
    {
        using OpenAiChatClientFactory factory = new(ConfigurationLoader.LoadYaml(TwoModelsYaml), FakeKey);

        var failure = Assert.Throws<ConfigurationLoadException>(
            () => factory.GetChatClient(new ModelReference { Ref = "summarise" }));

        Assert.Contains("'summarise'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("'reply'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("'fill'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKindThisAdapterDoesNotServe_FailsWhenTheAdapterIsBuilt()
    {
        var document = ConfigurationLoader.LoadYaml(OtherVendorYaml);

        var failure = Assert.Throws<ConfigurationLoadException>(() => new OpenAiChatClientFactory(document, FakeKey));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/providers/llm/0/kind", error.Pointer);
        Assert.Contains("anthropic", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheApiKey_ComesFromTheResolverChain()
    {
        MapSecretResolver resolver = new();
        resolver.With(OpenAiChatClientFactory.ApiKeySecretName, FakeKey);

        using var factory = await OpenAiChatClientFactory.CreateAsync(
            ConfigurationLoader.LoadYaml(TwoModelsYaml),
            resolver,
            TestContext.Current.CancellationToken);

        Assert.NotNull(factory.GetChatClient(new ModelReference { Ref = "reply" }));
    }

    [Fact]
    public async Task NoApiKeyAnywhere_FailsAndSaysWhereToPutOne()
    {
        var saved = Environment.GetEnvironmentVariable(OpenAiChatClientFactory.ApiKeyVariableName);
        Environment.SetEnvironmentVariable(OpenAiChatClientFactory.ApiKeyVariableName, null);

        try
        {
            var failure = await Assert.ThrowsAsync<SecretResolutionException>(
                async () => await OpenAiChatClientFactory.CreateAsync(
                    ConfigurationLoader.LoadYaml(TwoModelsYaml),
                    new MapSecretResolver(),
                    TestContext.Current.CancellationToken));

            Assert.Contains(OpenAiChatClientFactory.ApiKeySecretName, failure.Message, StringComparison.Ordinal);
            Assert.Contains(OpenAiChatClientFactory.ApiKeyVariableName, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpenAiChatClientFactory.ApiKeyVariableName, saved);
        }
    }
}

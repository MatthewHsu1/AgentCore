using AgentCore.TestSupport;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Embeddings;
using AgentCore.Application.Secrets;
using AgentCore.Infrastructure.Llm.OpenAI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Llm.OpenAI;

/// <summary>
/// The OpenAI embedding adapter. It owns the vendor only: the SDK client, the key, the model name.
/// </summary>
/// <remarks>
/// Building a generator opens no socket, so every test here runs offline. The key is a fake string,
/// and no test sends a request.
/// </remarks>
public sealed class OpenAiEmbeddingGeneratorAdapterTests
{
    private const string FakeKey = "sk-test-not-a-real-key";

    private const string OneGeneratorYaml =
        """
        apiVersion: agentcore/v1
        name: one-generator
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          embeddings: { kind: openai, model: text-embedding-3-small, dimensions: 512 }
        """;

    [Fact]
    public void TheAdapter_ServesTheOpenAiKind()
    {
        Assert.Equal("openai", new OpenAiEmbeddingGeneratorAdapter().Kind);
    }

    [Fact]
    public async Task TheApiKey_ComesFromTheResolverChain()
    {
        MapSecretResolver resolver = new();
        resolver.With(OpenAiEmbeddingGeneratorAdapter.ApiKeySecretName, FakeKey);

        var generator = await new OpenAiEmbeddingGeneratorAdapter().CreateGeneratorAsync(
            Entry(),
            resolver,
            TestContext.Current.CancellationToken);

        Assert.NotNull(generator);
    }

    [Fact]
    public async Task TheGenerator_CarriesTheModelAndTheWidthTheDocumentWrote()
    {
        MapSecretResolver resolver = new();
        resolver.With(OpenAiEmbeddingGeneratorAdapter.ApiKeySecretName, FakeKey);

        var generator = await new OpenAiEmbeddingGeneratorAdapter().CreateGeneratorAsync(
            Entry(),
            resolver,
            TestContext.Current.CancellationToken);

        var metadata = generator.GetService(typeof(EmbeddingGeneratorMetadata)) as EmbeddingGeneratorMetadata;

        Assert.NotNull(metadata);
        Assert.Equal("text-embedding-3-small", metadata.DefaultModelId);
        Assert.Equal(512, metadata.DefaultModelDimensions);
    }

    [Fact]
    public async Task NoApiKeyAnywhere_FailsAndSaysWhereToPutOne()
    {
        var saved = Environment.GetEnvironmentVariable(OpenAiEmbeddingGeneratorAdapter.ApiKeyVariableName);
        Environment.SetEnvironmentVariable(OpenAiEmbeddingGeneratorAdapter.ApiKeyVariableName, null);

        try
        {
            var failure = await Assert.ThrowsAsync<SecretResolutionException>(
                async () => await new OpenAiEmbeddingGeneratorAdapter().CreateGeneratorAsync(
                    Entry(),
                    new MapSecretResolver(),
                    TestContext.Current.CancellationToken));

            Assert.Contains(OpenAiEmbeddingGeneratorAdapter.ApiKeySecretName, failure.Message, StringComparison.Ordinal);
            Assert.Contains(OpenAiEmbeddingGeneratorAdapter.ApiKeyVariableName, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpenAiEmbeddingGeneratorAdapter.ApiKeyVariableName, saved);
        }
    }

    [Fact]
    public async Task TheComposite_ServesAnOpenAiDocumentThroughThisAdapter()
    {
        MapSecretResolver resolver = new();
        resolver.With(OpenAiEmbeddingGeneratorAdapter.ApiKeySecretName, FakeKey);

        var generator = await CompositeEmbeddingGeneratorFactory.CreateAsync(
            Document(),
            resolver,
            [new OpenAiEmbeddingGeneratorAdapter()],
            TestContext.Current.CancellationToken);

        Assert.NotNull(generator);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------
    private static AgentCoreConfiguration Document() => ConfigurationLoader.LoadYaml(OneGeneratorYaml);

    private static EmbeddingProviderConfiguration Entry() => Document().Providers!.Embeddings!;
}

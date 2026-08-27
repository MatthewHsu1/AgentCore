using AgentCore.TestSupport;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Embeddings;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Embeddings.Fakes;
using Xunit;

namespace AgentCore.Application.Tests.Embeddings;

/// <summary>
/// The composite behind <c>UseEmbeddings</c>. It routes <c>providers.embeddings.kind</c> to the
/// adapter whose <see cref="IEmbeddingGeneratorAdapter.Kind"/> matches.
/// </summary>
/// <remarks>
/// Every adapter here is a fake, so every test runs offline. The document names the vendor and the
/// host registers the adapters; these tests prove the document alone decides which adapter answers.
/// </remarks>
public sealed class CompositeEmbeddingGeneratorFactoryTests
{
    private const string NoEmbeddingsYaml =
        """
        apiVersion: agentcore/v1
        name: no-embeddings
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        """;

    private const string OneKindYaml =
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
          embeddings: { kind: openai, model: text-embedding-3-small }
        """;

    private const string ShoutedKindYaml =
        """
        apiVersion: agentcore/v1
        name: shouted-generator
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          embeddings: { kind: OPENAI, model: text-embedding-3-small }
        """;

    // ---------------------------------------------------------------------------------------------
    // Routing: the document names the vendor, and the matching adapter builds it.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AKindWrittenInAnotherCase_StillFindsItsAdapter()
    {
        RecordingEmbeddingGeneratorAdapter openai = new("openai");

        var generator = await Create(ShoutedKindYaml, openai);

        Assert.Same(openai.Generator, generator);
    }

    [Fact]
    public async Task NoEmbeddingsBlockAtAll_BuildsNothingAndAsksNoAdapter()
    {
        RecordingEmbeddingGeneratorAdapter openai = new("openai");

        var generator = await Create(NoEmbeddingsYaml, openai);

        Assert.Null(generator);
        Assert.False(openai.CreateGeneratorCalled);
    }

    [Fact]
    public async Task TheAdapter_ReceivesTheEntryAndTheResolverChainTheHostBound()
    {
        RecordingEmbeddingGeneratorAdapter openai = new("openai");
        MapSecretResolver resolver = new();

        await Create(OneKindYaml, resolver, openai);

        Assert.Same(resolver, openai.LastSecrets);
        Assert.Equal("text-embedding-3-small", openai.LastEntry?.Model);
    }

    // ---------------------------------------------------------------------------------------------
    // What it refuses, at startup and never on the first call.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AKindNoAdapterServes_FailsAndNamesTheRegisteredKinds()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Create(OneKindYaml, new RecordingEmbeddingGeneratorAdapter("azure")));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/providers/embeddings/kind", error.Pointer);
        Assert.Contains("openai", error.Message, StringComparison.Ordinal);
        Assert.Contains("'azure'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoAdaptersOfOneKind_FailTheBuild()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(async () => await Create(
            OneKindYaml,
            new RecordingEmbeddingGeneratorAdapter("openai"),
            new RecordingEmbeddingGeneratorAdapter("OpenAI")));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/providers/embeddings/kind", error.Pointer);
        Assert.Contains("openai", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AHostThatRegistersNoAdapter_FailsAndSaysSo()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Create(OneKindYaml));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/providers/embeddings/kind", error.Pointer);
        Assert.Contains("no adapter", error.Message, StringComparison.Ordinal);
        Assert.Contains("options.UseEmbeddings(...)", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------
    private static ValueTask<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>?> Create(
        string yaml,
        params IEmbeddingGeneratorAdapter[] adapters)
        => Create(yaml, null, adapters);

    private static ValueTask<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>?> Create(
        string yaml,
        ISecretResolverPort? secrets,
        params IEmbeddingGeneratorAdapter[] adapters)
        => CompositeEmbeddingGeneratorFactory.CreateAsync(
            ConfigurationLoader.LoadYaml(yaml),
            secrets,
            adapters,
            TestContext.Current.CancellationToken);
}

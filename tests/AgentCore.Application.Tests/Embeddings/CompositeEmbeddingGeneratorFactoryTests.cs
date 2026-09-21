using AgentCore.TestSupport;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Embeddings;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Embeddings.Fakes;
using Xunit;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Embeddings
{
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
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        entries:
          main:
            agent: only
        """;

        private const string OneKindYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          conversation:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          embeddings: { kind: openai, model: text-embedding-3-small }
        entries:
          main:
            agent: only
        """;

        private const string ShoutedKindYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          conversation:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          embeddings: { kind: OPENAI, model: text-embedding-3-small }
        entries:
          main:
            agent: only
        """;

        // ---------------------------------------------------------------------------------------------
        // Routing: the document names the vendor, and the matching adapter builds it.
        // ---------------------------------------------------------------------------------------------
        [Fact]
        public async Task AKindWrittenInAnotherCase_StillFindsItsAdapter()
        {
            RecordingEmbeddingGeneratorAdapter openai = new("openai");

            IEmbeddingGenerator<string, Embedding<float>>? generator = await Create(ShoutedKindYaml, openai);

            Assert.Same(openai.Generator, generator);
        }

        [Fact]
        public async Task NoEmbeddingsBlockAtAll_BuildsNothingAndAsksNoAdapter()
        {
            RecordingEmbeddingGeneratorAdapter openai = new("openai");

            IEmbeddingGenerator<string, Embedding<float>>? generator = await Create(NoEmbeddingsYaml, openai);

            Assert.Null(generator);
            Assert.False(openai.CreateGeneratorCalled);
        }

        [Fact]
        public async Task TheAdapter_ReceivesTheEntryAndTheResolverChainTheHostBound()
        {
            RecordingEmbeddingGeneratorAdapter openai = new("openai");
            MapSecretResolver resolver = new();

            _ = await Create(OneKindYaml, resolver, openai);

            Assert.Same(resolver, openai.LastSecrets);
            Assert.Equal("text-embedding-3-small", openai.LastEntry?.Model);
        }

        // ---------------------------------------------------------------------------------------------
        // What it refuses, at startup and never on the first conversation.
        // ---------------------------------------------------------------------------------------------
        [Fact]
        public async Task AKindNoAdapterServes_FailsAndNamesTheRegisteredKinds()
        {
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                async () => await Create(OneKindYaml, new RecordingEmbeddingGeneratorAdapter("azure")));

            ConfigurationError error = Assert.Single(failure.Errors);
            Assert.Equal("/providers/embeddings/kind", error.Pointer);
            Assert.Contains("openai", error.Message, StringComparison.Ordinal);
            Assert.Contains("'azure'", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TwoAdaptersOfOneKind_FailTheBuild()
        {
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(async () => await Create(
                OneKindYaml,
                new RecordingEmbeddingGeneratorAdapter("openai"),
                new RecordingEmbeddingGeneratorAdapter("OpenAI")));

            ConfigurationError error = Assert.Single(failure.Errors);
            Assert.Equal("/providers/embeddings/kind", error.Pointer);
            Assert.Contains("openai", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task AHostThatRegistersNoAdapter_FailsAndSaysSo()
        {
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                async () => await Create(OneKindYaml));

            ConfigurationError error = Assert.Single(failure.Errors);
            Assert.Equal("/providers/embeddings/kind", error.Pointer);
            Assert.Contains("no adapter", error.Message, StringComparison.Ordinal);
            Assert.Contains("options.UseEmbeddings(...)", error.Message, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------------
        private static ValueTask<IEmbeddingGenerator<string, Embedding<float>>?> Create(
            string yaml,
            params IEmbeddingGeneratorAdapter[] adapters)
        {
            return Create(yaml, null, adapters);
        }

        private static ValueTask<IEmbeddingGenerator<string, Embedding<float>>?> Create(
            string yaml,
            ISecretResolverPort? secrets,
            params IEmbeddingGeneratorAdapter[] adapters)
        {
            return CompositeEmbeddingGeneratorFactory.CreateAsync(
                        ConfigurationLoader.LoadYaml(yaml),
                        secrets,
                        adapters,
                        TestContext.Current.CancellationToken);
        }
    }
}

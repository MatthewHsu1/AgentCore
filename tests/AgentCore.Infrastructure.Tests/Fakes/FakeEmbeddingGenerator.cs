using Microsoft.Extensions.AI;

namespace AgentCore.Infrastructure.Tests.Fakes;

/// <summary>
/// An embedding generator that answers one fixed vector, so a test reaches no OpenAI endpoint.
/// </summary>
/// <remarks>
/// <c>QdrantKnowledgeStore</c> and <c>QdrantKnowledgeAdapter</c> both take their generator from a
/// constructor for exactly this reason: the store embeds every query and the adapter embeds one probe
/// at startup, and neither should mean an OpenAI key to run a test. The vector is fixed, so a test's
/// ranking is decided by the corpus it wrote and by nothing else.
/// </remarks>
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly float[] _vector;

    public FakeEmbeddingGenerator(params float[] vector) => _vector = vector;

    /// <summary>Gets every value this generator was asked to embed, in call order.</summary>
    public List<string> Inputs { get; } = [];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        GeneratedEmbeddings<Embedding<float>> embeddings = [];
        foreach (var value in values)
        {
            Inputs.Add(value);
            embeddings.Add(new Embedding<float>(_vector));
        }

        return Task.FromResult(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        // Nothing to release.
    }
}

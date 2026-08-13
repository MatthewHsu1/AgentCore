using Microsoft.Extensions.AI;

namespace AgentCore.Infrastructure.Tests.Fakes;

/// <summary>
/// An embedding generator that answers one fixed vector, so a test reaches no OpenAI endpoint.
/// </summary>
/// <remarks>
/// The Zilliz retrieval store takes its generator from its constructor for exactly this reason, and
/// the Zilliz adapter takes one too. No test here embeds anything for real.
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

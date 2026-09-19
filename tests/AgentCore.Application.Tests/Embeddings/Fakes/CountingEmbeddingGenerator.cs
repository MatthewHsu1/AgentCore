using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Embeddings.Fakes;

/// <summary>
/// A vendor that counts how many texts it was asked to embed and answers each with its length.
/// </summary>
internal sealed class CountingEmbeddingGenerator(string defaultModel) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly EmbeddingGeneratorMetadata _metadata = new(defaultModelId: defaultModel);

    /// <summary>Gets how many texts reached the vendor, across every call.</summary>
    public int TextsEmbedded { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var texts = values.ToList();
        TextsEmbedded += texts.Count;
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            texts.Select(text => new Embedding<float>(new float[] { text.Length }))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata : null;

    public void Dispose()
    {
    }
}

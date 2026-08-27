using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Embeddings.Fakes;

/// <summary>
/// An offline embedding vendor. It answers one <c>kind</c> and records what it was asked to build.
/// </summary>
internal sealed class RecordingEmbeddingGeneratorAdapter : IEmbeddingGeneratorAdapter
{
    public RecordingEmbeddingGeneratorAdapter(string kind)
        => Kind = kind;

    public string Kind { get; }

    /// <summary>Gets whether the composite asked this adapter to build.</summary>
    public bool CreateGeneratorCalled { get; private set; }

    /// <summary>Gets the entry the last build received.</summary>
    public EmbeddingProviderConfiguration? LastEntry { get; private set; }

    /// <summary>Gets the resolver chain the last build received.</summary>
    public ISecretResolverPort? LastSecrets { get; private set; }

    /// <summary>Gets the object the last build returned.</summary>
    public IEmbeddingGenerator<string, Embedding<float>>? Generator { get; private set; }

    public ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateGeneratorAsync(
        EmbeddingProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        CreateGeneratorCalled = true;
        LastEntry = entry;
        LastSecrets = secrets;
        Generator = new FakeEmbeddingGenerator();
        return ValueTask.FromResult(Generator);
    }
}

/// <summary>A generator that answers every value with the same three-wide vector.</summary>
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private static readonly float[] Vector = [0f, 0f, 0f];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            values.Select(_ => new Embedding<float>(Vector))));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

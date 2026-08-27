using System.ClientModel;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentCore.Infrastructure.Llm.OpenAI;

/// <summary>
/// The OpenAI adapter behind <see cref="IEmbeddingGeneratorAdapter"/>.
/// </summary>
public sealed class OpenAiEmbeddingGeneratorAdapter : IEmbeddingGeneratorAdapter
{
    /// <summary>The one <c>providers.embeddings.kind</c> value this adapter serves.</summary>
    public const string ProviderKind = "openai";

    /// <summary>The <c>${secret:name}</c> name the resolver chain is asked for.</summary>
    public const string ApiKeySecretName = KnownSecrets.OpenAiApiKeyName;

    /// <summary>The standard OpenAI environment variable, read when the chain holds no name.</summary>
    public const string ApiKeyVariableName = KnownSecrets.OpenAiApiKeyVariable;

    /// <inheritdoc/>
    public string Kind => ProviderKind;

    /// <summary>Builds the generator of the one <c>providers.embeddings</c> block.</summary>
    /// <param name="entry">The block, whose <c>kind</c> named this adapter.</param>
    /// <param name="secrets">The resolver chain, or <see langword="null"/> to read the environment only.</param>
    /// <param name="cancellationToken">Cancels the key read.</param>
    /// <returns>The generator. The host owns and disposes it.</returns>
    /// <exception cref="SecretResolutionException">Neither the chain nor the environment holds a key.</exception>
    public async ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateGeneratorAsync(
        EmbeddingProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var client = new OpenAIClient(new ApiKeyCredential(
            await secrets
                .RequireAsync(KnownSecrets.OpenAi, cancellationToken: cancellationToken)
                .ConfigureAwait(false)));

        return client
            .GetEmbeddingClient(entry.Model)
            .AsIEmbeddingGenerator(entry.Dimensions);
    }
}

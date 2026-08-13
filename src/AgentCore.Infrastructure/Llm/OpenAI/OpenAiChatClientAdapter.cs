using System.ClientModel;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentCore.Infrastructure.Llm.OpenAI;

/// <summary>
/// The OpenAI adapter behind <see cref="IChatClientAdapter"/>.
/// </summary>
/// <remarks>
/// <para>
/// This class owns the vendor only: the SDK client, the key, the model name of one entry. The
/// <c>as</c> map, the default entry, the temperature wrapper, and the client cache live in
/// <c>CompositeChatClientFactory</c>, which calls this adapter once for each <c>providers.llm[]</c>
/// entry whose <c>kind</c> is <see cref="ProviderKind"/>. The seam therefore moves to another vendor
/// by registering another adapter, and no code changes.
/// </para>
/// <para>
/// The API key never appears in this file. The first build asks the
/// <see cref="ISecretResolverPort"/> chain for <see cref="ApiKeySecretName"/>, then falls back to
/// the <see cref="ApiKeyVariableName"/> variable every OpenAI tool already reads. No message and no
/// exception of this class carries the value. One vendor client serves every entry, so the key
/// resolves once and a call costs one connection pool.
/// </para>
/// </remarks>
public sealed class OpenAiChatClientAdapter : IChatClientAdapter
{
    /// <summary>The one <c>providers.llm[].kind</c> value this adapter serves.</summary>
    public const string ProviderKind = "openai";

    /// <summary>The <c>${secret:name}</c> name the resolver chain is asked for.</summary>
    public const string ApiKeySecretName = "openai-api-key";

    /// <summary>The standard OpenAI environment variable, read when the chain holds no name.</summary>
    public const string ApiKeyVariableName = "OPENAI_API_KEY";

    private OpenAIClient? _client;

    /// <inheritdoc/>
    public string Kind => ProviderKind;

    /// <summary>Builds the client of one entry, reading the key on the first build only.</summary>
    /// <param name="entry">The entry, whose <c>kind</c> named this adapter.</param>
    /// <param name="secrets">The resolver chain, or <see langword="null"/> to read the environment only.</param>
    /// <param name="cancellationToken">Cancels the key read.</param>
    /// <returns>The client. The composite owns and disposes it.</returns>
    /// <exception cref="SecretResolutionException">Neither the chain nor the environment holds a key.</exception>
    /// <remarks>
    /// The composite builds sequentially while the host starts, so this method is not called from
    /// two threads at once and the one-client cache needs no lock.
    /// </remarks>
    public async ValueTask<IChatClient> CreateClientAsync(
        LlmProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _client ??= new OpenAIClient(new ApiKeyCredential(
            await ResolveKeyAsync(secrets, cancellationToken).ConfigureAwait(false)));

        return _client.GetChatClient(entry.Model).AsIChatClient();
    }

    /// <summary>Reads the key through the chain and then the environment.</summary>
    /// <param name="secrets">The resolver chain, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The key.</returns>
    /// <exception cref="SecretResolutionException">Neither place holds one.</exception>
    private static async ValueTask<string> ResolveKeyAsync(
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken)
    {
        string? key = null;
        if (secrets is not null)
        {
            key = await secrets.TryResolveAsync(ApiKeySecretName, cancellationToken).ConfigureAwait(false);
        }

        key ??= Environment.GetEnvironmentVariable(ApiKeyVariableName);
        if (key is not { Length: > 0 })
        {
            throw new SecretResolutionException(
                "the OpenAI API key did not resolve. Bind a resolver that holds '" + ApiKeySecretName
                + "', or set the " + ApiKeyVariableName + " variable. This adapter holds no key of its own.");
        }

        return key;
    }
}

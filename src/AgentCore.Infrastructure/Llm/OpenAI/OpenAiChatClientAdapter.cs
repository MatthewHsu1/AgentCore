// The reasoning-effort type is marked for evaluation by the SDK (OPENAI001). It is pinned at
// OpenAI 2.12.0 and covered by OpenAiReasoningEffortTests, which fail loudly if a bump moves it.
#pragma warning disable OPENAI001

using System.ClientModel;
using System.Globalization;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

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
/// The API key never appears in this file. The first build hands
/// <see cref="KnownSecrets.OpenAi"/> to <see cref="SecretResolverExtensions.RequireAsync"/>, which
/// asks the <see cref="ISecretResolverPort"/> chain and then falls back to the variable every OpenAI
/// tool already reads. No message and no exception of this class carries the value. One vendor
/// client serves every entry, so the key resolves once and a call costs one connection pool.
/// </para>
/// </remarks>
public sealed class OpenAiChatClientAdapter : IChatClientAdapter
{
    /// <summary>The one <c>providers.llm[].kind</c> value this adapter serves.</summary>
    public const string ProviderKind = "openai";

    /// <summary>The <c>${secret:name}</c> name the resolver chain is asked for.</summary>
    public const string ApiKeySecretName = KnownSecrets.OpenAiApiKeyName;

    /// <summary>The standard OpenAI environment variable, read when the chain holds no name.</summary>
    public const string ApiKeyVariableName = KnownSecrets.OpenAiApiKeyVariable;

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
            await secrets
                .RequireAsync(KnownSecrets.OpenAi, cancellationToken: cancellationToken)
                .ConfigureAwait(false)));

        var client = _client.GetResponsesClient().AsIChatClient(entry.Model);

        return entry.ReasoningEffort is { Length: > 0 } effort ? WithReasoningEffort(client, effort) : client;
    }

    /// <summary>Puts <c>reasoning_effort</c> on every request this client sends.</summary>
    /// <param name="client">The client of one entry.</param>
    /// <param name="effort">The value the document wrote.</param>
    /// <returns>The client the factory hands out.</returns>
    /// <exception cref="ConfigurationLoadException">The value is not one this vendor knows.</exception>
    internal static IChatClient WithReasoningEffort(IChatClient client, string effort)
    {
        var level = Level(effort);

        return client
            .AsBuilder()
            .ConfigureOptions(options => options.RawRepresentationFactory ??=
                _ => new CreateResponseOptions
                {
                    ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = level },
                })
            .Build();
    }

    /// <summary>Reads one <c>reasoningEffort</c> value.</summary>
    /// <param name="effort">The value the document wrote.</param>
    /// <returns>The vendor level.</returns>
    /// <exception cref="ConfigurationLoadException">The value is not one this vendor knows.</exception>
    private static ResponseReasoningEffortLevel Level(string effort)
        => effort.ToLowerInvariant() switch
        {
            "none" => ResponseReasoningEffortLevel.None,
            "minimal" => ResponseReasoningEffortLevel.Minimal,
            "low" => ResponseReasoningEffortLevel.Low,
            "medium" => ResponseReasoningEffortLevel.Medium,
            "high" => ResponseReasoningEffortLevel.High,
            _ => throw new ConfigurationLoadException(new ConfigurationError
            {
                Pointer = "/providers/llm",
                Message = string.Format(
                    CultureInfo.InvariantCulture,
                    "reasoningEffort '{0}' is not one this vendor knows. Write none, minimal, low, "
                    + "medium or high.",
                    effort),
                Check = ConfigurationCheck.ReferenceResolution,
            }),
        };
}

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

        return WithResponseDefaults(client, entry.ReasoningEffort);
    }

    /// <summary>Puts <c>store</c> and <c>reasoning_effort</c> on every request this client sends.</summary>
    internal static IChatClient WithResponseDefaults(IChatClient client, string? effort)
    {
        var level = effort is { Length: > 0 } value ? Level(value) : (ResponseReasoningEffortLevel?)null;

        return client
            .AsBuilder()
            .ConfigureOptions(options =>
            {
                var caller = options.RawRepresentationFactory;

                options.RawRepresentationFactory = inner =>
                {
                    if (caller?.Invoke(inner) is not CreateResponseOptions raw)
                    {
                        raw = new CreateResponseOptions();
                    }

                    raw.StoredOutputEnabled ??= false;

                    if (level is { } chosen)
                    {
                        raw.ReasoningOptions ??= new ResponseReasoningOptions
                        {
                            ReasoningEffortLevel = chosen,
                        };
                    }

                    return raw;
                };
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

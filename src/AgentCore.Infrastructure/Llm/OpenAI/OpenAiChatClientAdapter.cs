// The reasoning-effort type is marked for evaluation by the SDK (OPENAI001). It is pinned at
// OpenAI 2.13.0 and covered by OpenAiReasoningEffortTests, which fail loudly if a bump moves it.
#pragma warning disable OPENAI001

using System.ClientModel;
using System.Globalization;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Llm;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace AgentCore.Infrastructure.Llm.OpenAI
{
    /// <summary>
    /// The OpenAI adapter behind <see cref="IChatClientAdapter"/>.
    /// </summary>
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

            IChatClient client = _client.GetResponsesClient().AsIChatClient(entry.Model);

            return WithResponseDefaults(client, entry.ReasoningEffort);
        }

        /// <inheritdoc />
        public AITool? ResolveHostedTool(AITool marker, LlmProviderConfiguration entry)
        {
            ArgumentNullException.ThrowIfNull(marker);
            ArgumentNullException.ThrowIfNull(entry);

            return marker switch
            {
                HostedWebSearchTool when entry.WebSearch != false => marker,
                _ => null,
            };
        }

        /// <summary>Puts <c>store</c>, <c>reasoning_effort</c> and <c>prompt_cache_key</c> on every request this client sends.</summary>
        internal static IChatClient WithResponseDefaults(IChatClient client, string? effort)
        {
            ResponseReasoningEffortLevel? level = effort is { Length: > 0 } value ? Level(value) : (ResponseReasoningEffortLevel?)null;

            return client
                .AsBuilder()
                .ConfigureOptions(options =>
                {
                    Func<IChatClient, object?>? caller = options.RawRepresentationFactory;

                    options.RawRepresentationFactory = inner =>
                    {
                        if (caller?.Invoke(inner) is not CreateResponseOptions raw)
                        {
                            raw = new CreateResponseOptions();
                        }

                        raw.StoredOutputEnabled ??= false;

                        if (options.AdditionalProperties?.TryGetValue(ChatRequestProperties.ConversationId, out string? conversationId) == true)
                        {
                            raw.PromptCacheKey ??= conversationId;
                        }

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
        {
            return effort.ToLowerInvariant() switch
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
    }
}

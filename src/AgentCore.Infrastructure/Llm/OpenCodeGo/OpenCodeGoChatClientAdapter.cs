// The reasoning-effort type is marked for evaluation by the SDK (OPENAI001). It is pinned at
// OpenAI 2.13.0 and covered by OpenCodeGoReasoningEffortTests, which fail loudly if a bump moves it.
#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Llm;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Responses;

namespace AgentCore.Infrastructure.Llm.OpenCodeGo
{
    /// <summary>
    /// The OpenCode Go adapter behind <see cref="IChatClientAdapter"/>.
    /// </summary>
    public sealed class OpenCodeGoChatClientAdapter : IChatClientAdapter, IDisposable
    {
        /// <summary>The one <c>providers.llm[].kind</c> value this adapter serves.</summary>
        public const string ProviderKind = "opencode-go";

        /// <summary>The <c>${secret:name}</c> name the resolver chain is asked for.</summary>
        public const string ApiKeySecretName = KnownSecrets.OpenCodeGoApiKeyName;

        /// <summary>The environment variable OpenCode Go's own docs name.</summary>
        public const string ApiKeyVariableName = KnownSecrets.OpenCodeGoApiKeyVariable;

        /// <summary>The name this adapter opens its client under, on the pipeline of the host.</summary>
        public const string HttpClientName = "agentcore.opencode-go";

        /// <summary> The header OpenCode Go's own docs ask every client to send. </summary>
        public const string SessionHeaderName = "x-opencode-session";

        /// <summary>The user agent OpenCode Go's own docs ask every client to identify itself with.</summary>
        public const string UserAgentValue = "agentcore/1.0";

        /// <summary>The configuration key a host names to override <see cref="DefaultApiEndpoint"/>.</summary>
        public const string ApiEndpointConfigurationKey = "AgentCore:OpenCodeGo:ApiEndpoint";

        /// <summary>The endpoint this adapter posts to, unless a host names its own.</summary>
        public static readonly Uri DefaultApiEndpoint = new("https://opencode.ai/zen/go/v1", UriKind.Absolute);

        /// <summary>The conversation id the header policy reads on send. </summary>
        private readonly AsyncLocal<string?> _currentSessionId = new();

        private readonly IHttpMessageHandlerFactory _handlers;

        private readonly Uri _apiEndpoint;

        private HttpClient? _transport;

        private ApiKeyCredential? _credential;

        private OpenAIClient? _vendorClient;

        /// <summary>Creates the adapter over the outbound pipeline of the host.</summary>
        /// <param name="handlers">The pipeline that holds the connection lifetime, the deadline, and the retry.</param>
        /// <param name="apiEndpoint">
        /// The endpoint to post to. Defaults to <see cref="DefaultApiEndpoint"/> when omitted.
        /// </param>
        public OpenCodeGoChatClientAdapter(IHttpMessageHandlerFactory handlers, Uri? apiEndpoint = null)
        {
            ArgumentNullException.ThrowIfNull(handlers);
            _handlers = handlers;
            _apiEndpoint = apiEndpoint ?? DefaultApiEndpoint;
        }

        /// <summary>Creates the adapter, reading <see cref="ApiEndpointConfigurationKey"/> off the host's own configuration.</summary>
        /// <param name="handlers">The pipeline that holds the connection lifetime, the deadline, and the retry.</param>
        /// <param name="configuration">The host's own configuration.</param>
        /// <returns>The adapter.</returns>
        public static OpenCodeGoChatClientAdapter CreateFromConfiguration(IHttpMessageHandlerFactory handlers, IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            Uri? apiEndpoint = configuration[ApiEndpointConfigurationKey] is { } value
                ? new Uri(value, UriKind.Absolute)
                : null;

            return new OpenCodeGoChatClientAdapter(handlers, apiEndpoint);
        }

        /// <inheritdoc/>
        public string Kind => ProviderKind;

        /// <summary>Releases the shared transport. The vendor clients built over it release nothing of their own.</summary>
        public void Dispose()
        {
            _transport?.Dispose();
        }

        /// <summary>Builds the client of one entry, reading the key on the first build only.</summary>
        public async ValueTask<IChatClient> CreateClientAsync(
            LlmProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);

            if (_transport is null)
            {
                string apiKey = await secrets
                    .RequireAsync(KnownSecrets.OpenCodeGo, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                _transport = new HttpClient(_handlers.CreateHandler(HttpClientName), disposeHandler: false);

                _credential = new ApiKeyCredential(apiKey);
            }

            return new ConversationRoutedChatClient(
                () => BuildClientFor(entry.Model, entry.ReasoningEffort), _currentSessionId);
        }

        /// <summary>
        /// Gets the vendor client of one model, built fresh over the one shared vendor connection.
        /// It speaks the Responses API: OpenCode Go serves some models on that route only.
        /// </summary>
        private IChatClient BuildClientFor(string model, string? effort)
        {
            _vendorClient ??= CreateVendorClient();

            return WithResponseDefaults(_vendorClient.GetResponsesClient().AsIChatClient(model), effort);
        }

        /// <summary>Builds the one vendor connection every model shares, with the session header wired to <see cref="_currentSessionId"/>.</summary>
        private OpenAIClient CreateVendorClient()
        {
            OpenAIClientOptions options = new()
            {
                Endpoint = _apiEndpoint,
                Transport = new HttpClientPipelineTransport(_transport!),
            };

            options.AddPolicy(new OpenCodeGoHeaderPolicy(_currentSessionId), PipelinePosition.BeforeTransport);

            return new OpenAIClient(_credential!, options);
        }

        /// <summary>
        /// Puts <c>store: false</c> and <c>reasoning.effort</c> on every request this client sends, unless the
        /// caller already set them. Without <c>store: false</c> the vendor client reads the response id as a
        /// server-side conversation, which clashes with the history AgentCore keeps itself.
        /// </summary>
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

        /// <summary>Writes the two headers OpenCode Go's own docs ask for, last, so nothing after this undoes them.</summary>
        private sealed class OpenCodeGoHeaderPolicy(AsyncLocal<string?> currentSessionId) : PipelinePolicy
        {
            public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
            {
                Annotate(message);
                ProcessNext(message, pipeline, currentIndex);
            }

            public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
            {
                Annotate(message);
                return ProcessNextAsync(message, pipeline, currentIndex);
            }

            private void Annotate(PipelineMessage message)
            {
                string sessionId = currentSessionId.Value
                    ?? throw new InvalidOperationException(
                        "OpenCode Go requires a conversation id set on the current call before it reaches the transport.");

                message.Request.Headers.Set(SessionHeaderName, sessionId);
                message.Request.Headers.Set("User-Agent", UserAgentValue);
            }
        }

        /// <summary>Routes each call to the vendor client of its model, after stamping the conversation id the header policy reads.</summary>
        private sealed class ConversationRoutedChatClient(
            Func<IChatClient> resolve,
            AsyncLocal<string?> currentSessionId) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                currentSessionId.Value = RequireConversationId(options);
                return resolve().GetResponseAsync(messages, options, cancellationToken);
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                currentSessionId.Value = RequireConversationId(options);

                await foreach (ChatResponseUpdate update in resolve()
                    .GetStreamingResponseAsync(messages, options, cancellationToken)
                    .ConfigureAwait(false))
                {
                    yield return update;
                }
            }

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                ArgumentNullException.ThrowIfNull(serviceType);
                return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
            }

            public void Dispose()
            {
                // The vendor client this routes to shares the adapter's one transport, which the
                // adapter — not any conversation's view of it — owns.
            }

            private static string RequireConversationId(ChatOptions? options)
            {
                if (options?.AdditionalProperties?.TryGetValue(
                        ChatRequestProperties.ConversationId, out string? conversationId) != true
                    || conversationId is not { Length: > 0 })
                {
                    throw new InvalidOperationException(
                        $"OpenCode Go requires a conversation id on every request. Set {ChatRequestProperties.ConversationId} in ChatOptions.AdditionalProperties.");
                }

                return conversationId;
            }
        }
    }
}

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Llm;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using ChatClient = OpenAI.Chat.ChatClient;

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

        /// <summary>One vendor client per (model, conversation) pair, built once and reused.</summary>
        private readonly ConcurrentDictionary<(string Model, string SessionId), ChatClient> _clients = new();

        private readonly IHttpMessageHandlerFactory _handlers;

        private readonly Uri _apiEndpoint;

        private HttpClient? _transport;

        private ApiKeyCredential? _credential;

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

            return new ConversationRoutedChatClient(entry.Model, ClientFor);
        }

        /// <summary>Gets the vendor client of one (model, conversation) pair, building it on the first ask.</summary>
        private ChatClient ClientFor(string model, string sessionId)
        {
            return _clients.GetOrAdd((model, sessionId), key =>
            {
                OpenAIClientOptions options = new()
                {
                    Endpoint = _apiEndpoint,
                    Transport = new HttpClientPipelineTransport(_transport!),
                };

                options.AddPolicy(new OpenCodeGoHeaderPolicy(key.SessionId), PipelinePosition.BeforeTransport);

                return new OpenAIClient(_credential!, options).GetChatClient(key.Model);
            });
        }

        /// <summary>Writes the two headers OpenCode Go's own docs ask for, last, so nothing after this undoes them.</summary>
        private sealed class OpenCodeGoHeaderPolicy(string sessionId) : PipelinePolicy
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
                message.Request.Headers.Set(SessionHeaderName, sessionId);
                message.Request.Headers.Set("User-Agent", UserAgentValue);
            }
        }

        /// <summary>Routes each call to the vendor client of the conversation it belongs to.</summary>
        private sealed class ConversationRoutedChatClient(
            string model,
            Func<string, string, ChatClient> resolve) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                return Route(options).GetResponseAsync(messages, options, cancellationToken);
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                return Route(options).GetStreamingResponseAsync(messages, options, cancellationToken);
            }

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                ArgumentNullException.ThrowIfNull(serviceType);
                return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
            }

            public void Dispose()
            {
                // The vendor clients this routes to share the adapter's one transport, which the
                // adapter — not any conversation's view of it — owns.
            }

            private IChatClient Route(ChatOptions? options)
            {
                if (options?.AdditionalProperties?.TryGetValue(
                        ChatRequestProperties.ConversationId, out string? conversationId) != true
                    || conversationId is not { Length: > 0 })
                {
                    throw new InvalidOperationException(
                        $"OpenCode Go requires a conversation id on every request. Set {ChatRequestProperties.ConversationId} in ChatOptions.AdditionalProperties.");
                }

                return resolve(model, conversationId).AsIChatClient();
            }
        }
    }
}

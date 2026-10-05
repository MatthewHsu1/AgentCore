using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Webhook;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using AgentCore.AspNetCore.Voice.Ports;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive
{
    /// <summary>
    /// OpenAI GPT-Live over a SIP trunk as a conversation transport. GPT-Live speaks and takes
    /// turns itself; AgentCore answers its client delegations over the sideband, as text.
    /// </summary>
    public sealed class OpenAiLiveConversationAdapter : IConversationTransportAdapter
    {
        /// <summary>The one <c>providers.conversation.kind</c> value this vendor answers to.</summary>
        public const string OpenAiLiveKind = "openai-live";

        /// <summary>The name this adapter asks the outbound pipeline for.</summary>
        public const string HttpClientName = "openai-live";

        private readonly IHttpMessageHandlerFactory _handlers;

        private readonly Func<ISecretResolverPort?> _secrets;

        private readonly Func<LiveAttach, CancellationToken, ValueTask<ILiveSideband>> _attach;

        private readonly Uri _apiBase;

        /// <summary>Creates the adapter over the host's outbound pipeline.</summary>
        /// <param name="handlers">The pipeline that holds the connection lifetime, the deadline, and the retry.</param>
        /// <param name="secrets">The chain the two keys resolve through, or <see langword="null"/> for the environment alone.</param>
        public OpenAiLiveConversationAdapter(IHttpMessageHandlerFactory handlers, ISecretResolverPort? secrets = null)
            : this(handlers, () => secrets, WebSocketSideband.ConnectAsync, OpenAiLiveWire.ApiBase)
        {
        }

        /// <summary>Creates the adapter over a resolver read when the handler is built, not now.</summary>
        /// <param name="handlers">The pipeline that holds the connection lifetime, the deadline, and the retry.</param>
        /// <param name="secrets">Reads the chain the two keys resolve through, once the host has finished configuring.</param>
        internal OpenAiLiveConversationAdapter(IHttpMessageHandlerFactory handlers, Func<ISecretResolverPort?> secrets)
            : this(handlers, secrets, WebSocketSideband.ConnectAsync, OpenAiLiveWire.ApiBase)
        {
        }

        internal OpenAiLiveConversationAdapter(
            IHttpMessageHandlerFactory handlers,
            ISecretResolverPort? secrets,
            Func<LiveAttach, CancellationToken, ValueTask<ILiveSideband>> attach,
            Uri apiBase)
            : this(handlers, () => secrets, attach, apiBase)
        {
        }

        private OpenAiLiveConversationAdapter(
            IHttpMessageHandlerFactory handlers,
            Func<ISecretResolverPort?> secrets,
            Func<LiveAttach, CancellationToken, ValueTask<ILiveSideband>> attach,
            Uri apiBase)
        {
            ArgumentNullException.ThrowIfNull(handlers);
            ArgumentNullException.ThrowIfNull(secrets);
            ArgumentNullException.ThrowIfNull(attach);
            ArgumentNullException.ThrowIfNull(apiBase);

            _handlers = handlers;
            _secrets = secrets;
            _attach = attach;
            _apiBase = apiBase;
        }

        /// <inheritdoc />
        public string Kind => OpenAiLiveKind;

        /// <inheritdoc />
        public bool CarriesText => true;

        /// <inheritdoc />
        public CallTraits Traits => CallTraits.SpeaksForItself | CallTraits.TakesTurnsItself;

        /// <summary>Gets the keys being resolved once <see cref="CreateRoute"/> ran, or <see langword="null"/>.</summary>
        internal Task<OpenAiLiveCredentials>? Credentials { get; private set; }

        /// <inheritdoc />
        /// <exception cref="Application.Configuration.Parsing.ConfigurationLoadException">The <c>live:</c> block is missing or wrong.</exception>
        public ConversationRoute CreateRoute(ConversationProviderConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            OpenAiLiveSettings settings = OpenAiLiveSettings.From(configuration);
            Task<OpenAiLiveCredentials> credentials = OpenAiLiveCredentials.ResolveAsync(_secrets());
            Credentials = credentials;

            // The pipeline owns the handler chain, and other clients send on it, so this client disposes nothing.
            HttpClient http = new(_handlers.CreateHandler(HttpClientName), disposeHandler: false) { BaseAddress = _apiBase };
            OpenAiLiveWebhook webhook = new(settings, configuration, credentials, new OpenAiLiveControl(http, credentials), _attach, _apiBase);
            return new ConversationRoute(webhook.HandleAsync, webhook.IsOpenAiAsync) { Ready = credentials };
        }
    }
}

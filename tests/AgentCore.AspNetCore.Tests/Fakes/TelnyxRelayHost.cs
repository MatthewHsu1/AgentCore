using AgentCore.TestSupport;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.AspNetCore.Conversation;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Vendors.TelnyxRelay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// One real host with the relay route mapped, over a fake model.
    /// </summary>
    /// <remarks>
    /// Kestrel takes port zero and reports the port it got, so many tests run at once. The socket is
    /// real on purpose: framing, fragmentation, and the close handshake are wire behaviours, and the
    /// in-memory socket of TestServer never frames anything. No test here reaches a network, and no
    /// test needs a Telnyx account. That is T59.
    /// </remarks>
    internal sealed class TelnyxRelayHost : IAsyncDisposable
    {
        /// <summary>The vendor-neutral conversation route, with the document's one entry filled in.</summary>
        public const string MainConversation = "/v1/main/call";

        /// <summary>The relay's own route, with the document's one entry filled in.</summary>
        public const string MainRelay = "/v1/main/telnyx/relay";

        private readonly WebApplication _app;
        private readonly HttpClient _client;
        private readonly ErrorCapturingLoggerProvider _errors;

        private TelnyxRelayHost(WebApplication app, Uri socketAddress, HttpClient client, ErrorCapturingLoggerProvider errors)
        {
            _app = app;
            Address = socketAddress;
            _client = client;
            _errors = errors;
        }

        /// <summary>Gets the sessions the host resolved, so a test reads the live ones.</summary>
        public IConversationSessions Sessions => _app.Services.GetRequiredService<EntryRegistry>().ForSessions("main");

        /// <summary>Gets the <c>ws://</c> address of the relay route.</summary>
        /// <remarks>
        /// <see cref="ConnectAsync"/> covers every test that wants the vendor side driven for it. A
        /// test that needs to control exactly when bytes are read off the wire — proving something
        /// about backpressure, for one, since <see cref="FakeRelayClient"/>'s own pump always drains
        /// the socket as fast as it can — opens its own <see cref="System.Net.WebSockets.ClientWebSocket"/>
        /// against this address instead.
        /// </remarks>
        public Uri Address { get; }

        /// <summary>Gets the last exception any part of the host logged at error level, or null.</summary>
        /// <remarks>
        /// A host that forgot <c>app.UseWebSockets()</c> answers every relay request with an unhandled
        /// exception, and there is no field on the endpoint itself that carries it — only the log line
        /// ASP.NET Core's own hosting layer writes when a request delegate throws. This reads that line,
        /// captured by <see cref="ErrorCapturingLoggerProvider"/>, which this host always installs
        /// regardless of what a test's own <c>logging</c> callback adds.
        /// </remarks>
        public string? LastError => _errors.LastMessage;

        /// <summary>Starts one host over one document, with the WebSocket middleware in place.</summary>
        /// <param name="yaml">The document, as YAML.</param>
        /// <param name="reply">The model behind every agent.</param>
        /// <param name="configure">Anything else the test binds on the options.</param>
        /// <param name="logging">
        /// Anything a test adds to the logging pipeline, for example a provider that observes a line
        /// the connection writes. The default clears every provider, so nothing is captured unless a
        /// test asks for it here.
        /// </param>
        /// <param name="relay">Anything a test binds on the relay endpoint's own options.</param>
        /// <returns>The started host.</returns>
        public static Task<TelnyxRelayHost> StartAsync(
            string yaml,
            IChatClient reply,
            Action<AgentCoreOptions>? configure = null,
            Action<ILoggingBuilder>? logging = null,
            Action<TelnyxRelayOptions>? relay = null)
        {
            return StartCoreAsync(yaml, reply, configure, logging, relay, useWebSockets: true, useConversationSeam: false);
        }

        /// <summary>Starts one host that maps its route through <c>app.MapCall()</c> rather than the vendor extension.</summary>
        /// <param name="yaml">The document, as YAML. It must name <c>providers.conversation</c> and <c>providers.speech</c>.</param>
        /// <param name="reply">The model behind every agent.</param>
        /// <param name="configure">
        /// Anything else the test binds on the options. A test using this overload conversations
        /// <c>options.UseConversation(new TelnyxRelayConversationAdapter())</c> here, because nothing else registers the
        /// transport the seam is meant to pick.
        /// </param>
        /// <param name="logging">Anything a test adds to the logging pipeline.</param>
        /// <returns>The started host, answering on <see cref="MainConversation"/>.</returns>
        /// <remarks>
        /// <see cref="StartAsync"/> maps the relay through the internal <c>MapTelnyxRelay</c> and so
        /// bypasses the seam entirely, which is right for the several dozen frame-level tests that use
        /// it and wrong for proving that the shipped vendor is reachable from the vendor-neutral route.
        /// This overload is the one that joins the two.
        /// </remarks>
        public static Task<TelnyxRelayHost> StartThroughConversationSeamAsync(
            string yaml,
            IChatClient reply,
            Action<AgentCoreOptions>? configure = null,
            Action<ILoggingBuilder>? logging = null)
        {
            return StartCoreAsync(yaml, reply, configure, logging, relay: null, useWebSockets: true, useConversationSeam: true);
        }

        /// <summary>Starts one host the same way <see cref="StartAsync"/> does, but never calls <c>app.UseWebSockets()</c>.</summary>
        /// <param name="yaml">The document, as YAML.</param>
        /// <param name="reply">The model behind every agent.</param>
        /// <param name="configure">Anything else the test binds on the options.</param>
        /// <param name="logging">Anything a test adds to the logging pipeline.</param>
        /// <returns>The started host.</returns>
        /// <remarks>
        /// The middleware is the host's own job, not the library's — <c>MapTelnyxRelay</c> only maps the
        /// route. This is how a test proves what happens when a host forgets it.
        /// </remarks>
        public static Task<TelnyxRelayHost> StartWithoutWebSocketsAsync(
            string yaml,
            IChatClient reply,
            Action<AgentCoreOptions>? configure = null,
            Action<ILoggingBuilder>? logging = null)
        {
            return StartCoreAsync(yaml, reply, configure, logging, relay: null, useWebSockets: false, useConversationSeam: false);
        }

        private static async Task<TelnyxRelayHost> StartCoreAsync(
            string yaml,
            IChatClient reply,
            Action<AgentCoreOptions>? configure,
            Action<ILoggingBuilder>? logging,
            Action<TelnyxRelayOptions>? relay,
            bool useWebSockets,
            bool useConversationSeam)
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            _ = builder.WebHost.UseUrls("http://127.0.0.1:0");
            _ = builder.Logging.ClearProviders();
            logging?.Invoke(builder.Logging);

            ErrorCapturingLoggerProvider errors = new();
            _ = builder.Logging.AddProvider(errors);

            _ = builder.Services.AddAgentCore(options =>
            {
                options.Configuration = ConfigurationLoader.LoadYaml(yaml);
                _ = options.UseChatClients(_ => new RoutingChatClientFactory(reply));
                configure?.Invoke(options);
            });

            WebApplication app = builder.Build();

            if (useWebSockets)
            {
                // The library maps an endpoint. The middleware is the host's job, and a host that
                // forgets it gets an unhandled InvalidOperationException on every call instead — see
                // AHostThatForgotUseWebSockets_FailsWithAMessageThatNamesTheFix, which proves the
                // failing path in its own fact.
                _ = app.UseWebSockets(new WebSocketOptions
                {
                    KeepAliveInterval = TimeSpan.FromSeconds(5),
                    KeepAliveTimeout = TimeSpan.FromSeconds(5),
                });
            }

            string route;
            if (useConversationSeam)
            {
                // The vendor-neutral seam picks the transport out of providers.conversation and the adapter the
                // test registered. Nothing here names a route or a vendor.
                route = MainConversation;
                _ = app.MapCall();
            }
            else
            {
                route = MainRelay;
                TelnyxRelayOptions relayOptions = new();
                relay?.Invoke(relayOptions);
                _ = app.MapTelnyxRelay(TelnyxRelayEndpointRouteBuilderExtensions.DefaultPattern, relayOptions);
            }

            await app.StartAsync();

            string address = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .First();

            Uri httpAddress = new(address, UriKind.Absolute);
            Uri socket = new(
                address.Replace("http://", "ws://", StringComparison.Ordinal) + route);

            HttpClient client = new() { BaseAddress = httpAddress };

            return new TelnyxRelayHost(app, socket, client, errors);
        }

        /// <summary>Opens one relay socket to this host.</summary>
        /// <returns>The connected fake relay.</returns>
        public Task<FakeRelayClient> ConnectAsync()
        {
            return FakeRelayClient.ConnectAsync(Address);
        }

        /// <summary>Sends a plain HTTP GET to one route of this host.</summary>
        /// <param name="pattern">The route, for example the relay endpoint's own pattern.</param>
        /// <returns>The answer.</returns>
        public Task<HttpResponseMessage> GetAsync(string pattern)
        {
            return _client.GetAsync(pattern, TestContext.Current.CancellationToken);
        }

        /// <summary>Reads the session of one conversation, or null when the store dropped it.</summary>
        /// <param name="conversationId">The conversation id, which is the <c>conversationSessionId</c> of the setup frame.</param>
        /// <returns>The session, or null.</returns>
        public async Task<ConversationSession?> FindSessionAsync(string conversationId)
        {
            return await Sessions.TryGetAsync(conversationId, TestContext.Current.CancellationToken);
        }

        /// <summary>Waits until the store no longer holds one conversation.</summary>
        /// <param name="conversationId">The conversation id.</param>
        /// <returns>A task that completes when the session is gone.</returns>
        /// <exception cref="TimeoutException">The session was still there after two seconds.</exception>
        public async Task WaitForConversationEndAsync(string conversationId)
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                if (await FindSessionAsync(conversationId) is null)
                {
                    return;
                }

                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            throw new TimeoutException($"the session of conversation '{conversationId}' outlived its socket.");
        }

        /// <summary>Waits until the store holds one conversation.</summary>
        /// <param name="conversationId">The conversation id.</param>
        /// <returns>A task that completes once the session appears.</returns>
        /// <remarks>
        /// A setup frame's <c>SendAsync</c> completing on the client only means the bytes left the
        /// client; it proves nothing about whether the server has read and dispatched them yet. A test
        /// that then tears the socket down immediately would otherwise race the server's own session
        /// creation, and a pass would not prove the removal it meant to prove.
        /// </remarks>
        /// <exception cref="TimeoutException">The session never appeared within two seconds.</exception>
        public async Task WaitForSessionAsync(string conversationId)
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                if (await FindSessionAsync(conversationId) is not null)
                {
                    return;
                }

                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            throw new TimeoutException($"the session of conversation '{conversationId}' never appeared.");
        }

        /// <summary>Stops the host and releases the socket.</summary>
        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>Captures the message of the last exception any category logged at error level or above.</summary>
    /// <remarks>
    /// ASP.NET Core's own hosting layer logs an unhandled exception from a request delegate rather than
    /// letting a test observe it any other way, so this is the only handle <see cref="TelnyxRelayHost"/>
    /// has on it. Every category is captured, not just the relay connection's own logger, because the
    /// missing-middleware fault this exists for is thrown and logged by the framework, not by
    /// <c>AgentCore.TelnyxRelay</c>.
    /// </remarks>
    internal sealed class ErrorCapturingLoggerProvider : ILoggerProvider
    {
        private volatile string? _lastMessage;

        /// <summary>Gets the message of the last exception captured, or null when none was.</summary>
        public string? LastMessage => _lastMessage;

        public ILogger CreateLogger(string categoryName)
        {
            return new Logger(this);
        }

        public void Dispose()
        {
            // Nothing to release.
        }

        private sealed class Logger(ErrorCapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return logLevel >= LogLevel.Error;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (exception is not null)
                {
                    owner._lastMessage = exception.Message;
                }
            }
        }
    }
}

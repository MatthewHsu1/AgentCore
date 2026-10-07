using AgentCore.Application.Configuration.Parsing;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Vendors.TelnyxRelay;
using AgentCore.AspNetCore.Vendors.TelnyxRelay.Connection;
using AgentCore.AspNetCore.Voice.Routing;
using AgentCore.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// One <see cref="TelnyxRelayConnection"/> driven straight over a <see cref="FakeWebSocket"/>.
    /// </summary>
    internal sealed class RelayConnectionHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly TestHostLifetime _lifetime;
        private readonly CancellationTokenSource _requestAborted;

        private RelayConnectionHarness(
            ServiceProvider services,
            TestHostLifetime lifetime,
            CancellationTokenSource requestAborted,
            FakeWebSocket socket,
            Task connection)
        {
            _services = services;
            _lifetime = lifetime;
            _requestAborted = requestAborted;
            Socket = socket;
            Connection = connection;
        }

        /// <summary>Gets the socket a test queues frames on and reads sends off.</summary>
        public FakeWebSocket Socket { get; }

        /// <summary>Gets the container the connection resolves everything from.</summary>
        public IServiceProvider Services => _services;

        /// <summary>Gets the task that completes when the connection has torn itself down.</summary>
        public Task Connection { get; }

        /// <summary>Starts one connection over one document and one fake model.</summary>
        /// <param name="yaml">The document, as YAML.</param>
        /// <param name="reply">The model behind every agent.</param>
        /// <param name="logging">Anything a test adds to the logging pipeline.</param>
        /// <param name="relay">Anything a test binds on the relay endpoint's own options.</param>
        /// <param name="configure">
        /// Anything else the test binds on the container's own options, for example the clock every
        /// conversation and every connection then runs on.
        /// </param>
        /// <param name="services">
        /// Anything a test registers over what <c>AddAgentCore</c> registered. It runs after that conversation,
        /// so a registration here is the last one and the connection resolves it.
        /// </param>
        /// <param name="entry">The entry the route names, as <see cref="ConversationEndpointRouteBuilderExtensions.EntryOf"/> reads it.</param>
        /// <returns>The running harness.</returns>
        public static async Task<RelayConnectionHarness> StartAsync(
            string yaml,
            IChatClient reply,
            Action<ILoggingBuilder>? logging = null,
            Action<TelnyxRelayOptions>? relay = null,
            Action<AgentCoreOptions>? configure = null,
            Action<IServiceCollection>? services = null,
            string entry = SingleEntrySessionFactories.MainEntry)
        {
            ServiceCollection collection = new();
            _ = collection.AddLogging(builder =>
            {
                _ = builder.ClearProviders();
                logging?.Invoke(builder);
            });

            TestHostLifetime lifetime = new();
            _ = collection.AddSingleton<IHostApplicationLifetime>(lifetime);
            _ = collection.AddAgentCore(options =>
            {
                options.Configuration = ConfigurationLoader.LoadYaml(yaml);
                _ = options.UseChatClients(_ => new RoutingChatClientFactory(reply));
                configure?.Invoke(options);
            });

            services?.Invoke(collection);

            ServiceProvider provider = collection.BuildServiceProvider();

            // There is no host here, so nothing else would run the boot. This is the same hook a host
            // uses, in the same order, so the harness reaches a graph composed exactly as production
            // composes it.
            foreach (IHostedLifecycleService service in provider.GetServices<IHostedService>().OfType<IHostedLifecycleService>())
            {
                await service.StartingAsync(CancellationToken.None);
            }

            CancellationTokenSource requestAborted = new();
            DefaultHttpContext http = new() { RequestServices = provider, RequestAborted = requestAborted.Token };
            http.Request.RouteValues[ConversationEndpointRouteBuilderExtensions.EntryRouteParameter] = entry;

            TelnyxRelayOptions options = new();
            relay?.Invoke(options);

            FakeWebSocket socket = new();
            Task connection = TelnyxRelayConnection.RunAsync(http, socket, options);

            return new RelayConnectionHarness(provider, lifetime, requestAborted, socket, connection);
        }

        /// <summary>Aborts the request, as Kestrel does once the peer's connection drops.</summary>
        public void DropPeer()
        {
            _requestAborted.Cancel();
        }

        /// <summary>Stops the host, which is what the <c>EndpointUnavailable</c> close status reports.</summary>
        public void StopApplication()
        {
            _lifetime.StopApplication();
        }

        /// <summary>Ends the connection and releases everything behind it.</summary>
        public async ValueTask DisposeAsync()
        {
            // A test that failed early may have left the write loop parked or the read loop waiting, so
            // both are released here before anything waits on the connection task.
            Socket.ReleaseParkedState();
            Socket.QueueClose();

            try
            {
                await Connection.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The connection's own faults belong to whichever assertion a test already made about
                // them. Disposal only has to stop waiting.
            }

            Socket.Dispose();
            _requestAborted.Dispose();
            _lifetime.Dispose();
            await _services.DisposeAsync().ConfigureAwait(false);
        }
    }
}

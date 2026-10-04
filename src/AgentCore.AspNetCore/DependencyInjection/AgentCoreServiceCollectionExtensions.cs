using AgentCore.Application.Cache;
using AgentCore.Application.Audit;
using AgentCore.Application.Conversation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.Conversation;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Sessions;
using AgentCore.AspNetCore.Vendors.OpenAiLive;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AgentCore.AspNetCore.DependencyInjection
{
    /// <summary>
    /// The composition root. It turns one document into the services a host resolves.
    /// </summary>
    public static class AgentCoreServiceCollectionExtensions
    {
        /// <summary>Registers everything a conversation needs, and loads the document when the host starts.</summary>
        /// <param name="services">The service collection of the host.</param>
        /// <param name="configure">Binds the document and the adapters the document names.</param>
        /// <returns>The same collection, so a host chains its conversations.</returns>
        public static IServiceCollection AddAgentCore(
            this IServiceCollection services,
            Action<AgentCoreOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            _ = services.AddOptions();
            _ = services.Configure(configure);
            _ = services.AddOptions<AgentCoreOptions>()
                .Validate(
                    static o => o.ResponseRetention is null || o.ResponseRetention >= TimeSpan.Zero,
                    "AgentCoreOptions.ResponseRetention must be null or zero or greater.")
                .ValidateOnStart();
            _ = services.AddLogging();

            _ = services.AddSingleton<AgentCoreBoot>();
            _ = services.AddHostedService<AgentCoreBootService>();
            services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, EntryRouteCheck>());
            _ = services.AddSingleton(Boot(boot => boot.Configuration));
            _ = services.AddSingleton(Boot(boot => boot.Secrets));
            _ = services.AddSingleton(Boot(boot => boot.Bindings));
            _ = services.AddSingleton(Boot(boot => boot.CompiledRegistry));
            _ = services.AddSingleton(Boot(boot => boot.CompiledEntries));
            _ = services.AddSingleton(Boot(boot => boot.ChatClients));
            _ = services.AddSingleton(Boot(boot => boot.Guards));
            _ = services.AddSingleton(Boot(boot => boot.Tools));
            _ = services.AddSingleton(Boot(boot => boot.Conversations));
            _ = services.AddSingleton<IConversations>(provider => provider.GetRequiredService<Conversations>());
            _ = services.AddSingleton<IConversationStore>(provider => provider.GetRequiredService<Conversations>());
            _ = services.AddSingleton(Boot(boot => boot.Entries));
            _ = services.AddSingleton<IConversationSessionRegistry>(provider => provider.GetRequiredService<EntryRegistry>());
            _ = services.AddSingleton(Boot(boot => boot.AuditQueue));

            services.TryAddSingleton(provider =>
                new AgentCoreAgentSessionStore(provider.GetRequiredService<IConversationStore>()));

            services.TryAddSingleton<AgentSessionStore>(provider =>
                provider.GetRequiredService<AgentCoreAgentSessionStore>());

            _ = services.AddSingleton<IAuditSinkPort>(provider => provider.GetRequiredService<QueuedAuditSink>());

            _ = services.AddHostedService<ConversationSweeper>();

            // after the boot service: its StartAsync runs once the boot (StartingAsync) is done
            _ = services.AddSingleton<OpenAiLiveCalls>();
            _ = services.AddHostedService(provider => provider.GetRequiredService<OpenAiLiveCalls>());

            _ = services.AddSingleton(Boot(boot => boot.Telemetry!));
            _ = services.AddSingleton(Boot(boot => boot.Knowledge!));
            _ = services.AddSingleton(Boot(boot => boot.Blobs!));
            _ = services.AddSingleton(Boot(boot => boot.ConversationAdapters!));
            _ = services.AddSingleton(Boot(boot => boot.SpeechAdapters!));

            services.TryAddSingleton(provider =>
                provider.GetRequiredService<IOptions<AgentCoreOptions>>().Value.TimeProvider
                ?? TimeProvider.System);

            services.TryAddSingleton(provider =>
                provider.GetRequiredService<IOptions<AgentCoreOptions>>().Value.Cache
                ?? PassThroughHybridCache.Instance);

            services.TryAddSingleton<IConversationTitler>(provider => new ChatConversationTitler(
                provider.GetRequiredService<IConversationStore>(),
                provider.GetRequiredService<IChatClientFactory>()
                    .GetChatClient(provider.GetRequiredService<AgentCoreConfiguration>().Titler?.Model)));

            services.TryAddSingleton(Boot(boot => boot.Evaluators));

            services.TryAddSingleton(provider => new EvaluationSampler(
                provider.GetRequiredService<AgentCoreConfiguration>().Evaluation?.SampleRate
                ?? EvaluationConfiguration.DefaultSampleRate));

            services.TryAddSingleton<IEvaluationScorePublisher, InMemoryEvaluationScorePublisher>();

            return services;
        }

        /// <summary>Registers WebSocket options that suit a phone conversation rather than a browser tab.</summary>
        /// <param name="services">The service collection of the host.</param>
        /// <returns>The same collection, so a host chains its conversations.</returns>
        public static IServiceCollection AddAgentCoreWebSockets(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            return services.AddWebSockets(options =>
            {
                options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            });
        }

        /// <summary>Reads one thing out of the boot, once the host has started it.</summary>
        /// <typeparam name="T">What the caller is registering.</typeparam>
        /// <param name="read">Picks it off the started boot.</param>
        /// <returns>A factory the container conversations on first resolve.</returns>
        private static Func<IServiceProvider, T> Boot<T>(Func<AgentCoreBoot, T> read)
            where T : class
        {
            return provider =>
                    {
                        AgentCoreBoot boot = provider.GetRequiredService<AgentCoreBoot>();
                        return boot.Release(read(boot));
                    };
        }
    }
}

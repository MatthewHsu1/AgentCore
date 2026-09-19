using AgentCore.Application.Audit;
using AgentCore.Application.Conversation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Sessions;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentCore.AspNetCore.DependencyInjection;

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

        services.AddOptions();
        services.Configure(configure);
        services.AddLogging();

        services.AddSingleton<AgentCoreBoot>();
        services.AddHostedService<AgentCoreBootService>();
        services.AddSingleton(Boot(boot => boot.Configuration));
        services.AddSingleton(Boot(boot => boot.Secrets));
        services.AddSingleton(Boot(boot => boot.Bindings));
        services.AddSingleton(Boot(boot => boot.CompiledRegistry));
        services.AddSingleton(Boot(boot => boot.CompiledEntries));
        services.AddSingleton(Boot(boot => boot.ChatClients));
        services.AddSingleton(Boot(boot => boot.Guards));
        services.AddSingleton(Boot(boot => boot.Tools));
        services.AddSingleton(Boot(boot => boot.Conversations));
        services.AddSingleton<IConversations>(provider => provider.GetRequiredService<Conversations>());
        services.AddSingleton<IConversationStore>(provider => provider.GetRequiredService<Conversations>());
        services.AddSingleton(Boot(boot => boot.Entries));
        services.AddSingleton<IConversationSessionRegistry>(provider => provider.GetRequiredService<EntryRegistry>());
        services.AddSingleton(Boot(boot => boot.AuditQueue));

        services.TryAddSingleton(provider =>
            new AgentCoreAgentSessionStore(provider.GetRequiredService<IConversationStore>()));

        services.TryAddSingleton<AgentSessionStore>(provider =>
            provider.GetRequiredService<AgentCoreAgentSessionStore>());

        services.AddSingleton<IAuditSinkPort>(provider => provider.GetRequiredService<QueuedAuditSink>());


        services.AddSingleton(Boot(boot => boot.Telemetry!));
        services.AddSingleton(Boot(boot => boot.Knowledge!));
        services.AddSingleton(Boot(boot => boot.Blobs!));
        services.AddSingleton(Boot(boot => boot.ConversationAdapters!));
        services.AddSingleton(Boot(boot => boot.SpeechAdapters!));

        services.TryAddSingleton(provider =>
            provider.GetRequiredService<IOptions<AgentCoreOptions>>().Value.TimeProvider
            ?? TimeProvider.System);

        services.AddHostedService(provider => new ConversationSessionSweeper(
            provider,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<ConversationSessionSweeper>()
                ?? NullLogger<ConversationSessionSweeper>.Instance));

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
        => provider =>
        {
            var boot = provider.GetRequiredService<AgentCoreBoot>();
            return boot.Release(read(boot));
        };
}

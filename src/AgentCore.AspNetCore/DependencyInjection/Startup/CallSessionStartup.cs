using AgentCore.Application.Audit;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection.Startup;

/// <summary>The seam a call arrives on: the audit queue, the observers, and the session factory.</summary>
internal static class CallSessionStartup
{
    /// <summary>Opens the audit store the document names, reads the call, and registers the session factory.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The loaded document. It carries <c>providers.audit</c>.</param>
    /// <param name="options">The options the host filled. It carries the audit vendors, the clock, and any observer.</param>
    /// <param name="graph">The compiled graph and the seams step 5 made.</param>
    /// <param name="loggers">The factory the session and the audit queue take their loggers from.</param>
    /// <param name="cancellationToken">Cancels the store open.</param>
    internal static async ValueTask RegisterAsync(
        IServiceCollection services,
        AgentCoreConfiguration configuration,
        AgentCoreOptions options,
        CompiledGraph graph,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        IAuditSinkPort store = await AuditSinkFactory
            .OpenAsync(
                configuration,
                options.SecretResolver,
                options.AuditSinks ?? [],
                cancellationToken)
            .ConfigureAwait(false);

        if (store is InMemoryAuditSink && configuration.Providers?.Audit is null)
        {
            StartupLog.AuditSinkDefaulted(loggers.CreateLogger<QueuedAuditSink>());
        }

        QueuedAuditSink auditSink = new(store, loggers.CreateLogger<QueuedAuditSink>());

        // Composition, and so it happens here rather than inside the factory. The document named a
        // store, the host bound a logger, and neither named an ICallObserver; this is the line that
        // turns those into the readings of a call, in the order CallObservers.Standard argues for.
        var sessionLogger = loggers.CreateLogger<CallSession>();

        CallSessionFactory sessions = new(
            graph.Compiled,
            graph.Guards,
            CallSessionFactory.CreateExtractor(graph.Compiled, graph.ChatClients),
            options.TimeProvider,
            sessionLogger,
            CallObservers.Standard(auditSink, sessionLogger, options.Observers));

        services.AddSingleton<ICallSessionFactory>(sessions);

        // The same loop, behind the framework's own seam. A host that consumes AIAgent — a protocol
        // host, an evaluation harness — resolves this and never learns a second AgentCore type. It
        // is registered under its concrete type and not as AIAgent, because a host may hold other
        // agents and this one must not shadow them.
        services.AddSingleton(new AgentCoreAgent(sessions, configuration.Name));

        // The STORE is registered under its own concrete type, so a host or a test that wants to read
        // the chain back asks for the thing that holds it — GetRequiredService<InMemoryAuditSink>()
        // is how the events of one call are read now that the document, and not the host, builds it.
        services.AddSingleton(store.GetType(), store);

        // The QUEUE is what answers the port, not the store behind it. Resolving IAuditSinkPort must
        // give the thing that honours the port's contract, and appending straight to the store would
        // be the one path that sits on the caller. It is registered as the concrete type as well as
        // the port, so a host that wants FlushAsync before it reports success can ask for it.
        services.AddSingleton(auditSink);
        services.AddSingleton<IAuditSinkPort>(auditSink);

        // Neither the queue nor the store is owned by the container: it disposes only what it built,
        // so an instance handed to AddSingleton is never closed. Without this line the queue is never
        // drained, and every event accepted but not yet written is lost when the process stops.
        //
        // The order of the pair is the point. The queue drains INTO the store, so the store must
        // still be open while the drain runs; closing it first would fail exactly the writes this
        // owner exists to keep.
        StartupResourceOwner.Own(services, auditSink, store);
    }
}

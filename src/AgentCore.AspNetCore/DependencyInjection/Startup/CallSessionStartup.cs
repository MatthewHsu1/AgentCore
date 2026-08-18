using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.AspNetCore.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>The seam a call arrives on: the audit queue, the observers, and the session factory.</summary>
/// <remarks>
/// <see cref="CompiledAgent"/> is a process singleton by design, and it is registered as one.
/// <see cref="CallSession"/> is not, and it is registered nowhere: one call gets one session from
/// <see cref="ICallSessionFactory"/>, and <see cref="ICallSessionStore"/> holds it between requests.
/// </remarks>
internal static class CallSessionStartup
{
    /// <summary>Opens the audit store the document names, reads the call, and registers the session factory.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The loaded document. It carries <c>providers.audit</c>.</param>
    /// <param name="options">The options the host filled. It carries the audit vendors, the clock, and any observer.</param>
    /// <param name="graph">The compiled graph and the seams step 5 made.</param>
    /// <param name="evaluators">The registry the moderator comes out of.</param>
    /// <param name="loggers">The factory the session and the audit queue take their loggers from.</param>
    /// <param name="cancellationToken">Cancels the store open.</param>
    /// <remarks>
    /// <para>
    /// <b>There is always a sink.</b> <c>providers.audit.kind</c> picks one of the vendors the host
    /// registered, and a document that names none gets <see cref="AuditSinkFactory.MemoryKind"/>. The
    /// turn loop produces the events of D23 whatever a document says, so the seam that receives them
    /// has a working default rather than a null: that is what lets every reading of a call in
    /// <see cref="CallObservers.Standard"/> be unconditional, and what lets a first run and a test
    /// work with no database.
    /// </para>
    /// <para>
    /// The default is not durable, so it is announced. The in-process list grows without a bound and
    /// dies with the process, which is the wrong store for anything long-running and a silent one to
    /// fall into — a deployment that forgot the block would otherwise discover it by running out of
    /// memory. One warning at startup costs nothing and names the fix.
    /// </para>
    /// <para>
    /// This is the one place the "never sit on the turn" rule of <see cref="IAuditSinkPort"/> is
    /// applied. The seam opens a STORE, and this wraps it in the bounded channel and batching
    /// background writer of section 7, so a store that blocks on its database is a correct store and
    /// no adapter carries a queue of its own.
    /// </para>
    /// </remarks>
    internal static async ValueTask RegisterAsync(
        IServiceCollection services,
        AgentCoreConfiguration configuration,
        AgentCoreOptions options,
        CompiledGraph graph,
        EvaluatorRegistry evaluators,
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
            PromptModerator.FromRegistry(evaluators),
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
        // the port, so a host that wants FlushAsync before it reports success can ask for it, and so
        // the container disposes it on the way out — that disposal is what drains the queue and keeps
        // the accepted rows.
        services.AddSingleton(auditSink);
        services.AddSingleton<IAuditSinkPort>(auditSink);
    }
}

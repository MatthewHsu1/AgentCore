using AgentCore.Application.Audit;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.State;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection.Startup;

/// <summary>Everything the call seam opened: the audit chain, and the session factory over it.</summary>
/// <param name="Sessions">The factory that builds one session per call.</param>
/// <param name="Agent">The same turn loop, behind the framework's own agent seam.</param>
/// <param name="Queue">The queue that answers the audit port, not the store behind it.</param>
internal readonly record struct CallSessionSeam(
    ICallSessionFactory Sessions, AgentCoreAgent Agent, QueuedAuditSink Queue);

/// <summary>The seam a call arrives on: the audit queue, the observers, and the session factory.</summary>
internal static class CallSessionStartup
{
    /// <summary>Opens the audit store the document names and builds the session factory over it.</summary>
    /// <param name="boot">The owner the audit chain is tracked against.</param>
    /// <param name="configuration">The loaded document. It carries <c>providers.audit</c>.</param>
    /// <param name="options">The options the host filled. It carries the audit vendors, the clock, and any observer.</param>
    /// <param name="graph">The compiled graph and the seams step 5 made.</param>
    /// <param name="vocabulary">The cache <c>KnowledgeStartup.ApplyVocabularyAsync</c> filled.</param>
    /// <param name="loggers">The factory the session and the audit queue take their loggers from.</param>
    /// <param name="cancellationToken">Cancels the store open.</param>
    /// <returns>The session factory, the agent shim, and the queue in front of the store.</returns>
    internal static async ValueTask<CallSessionSeam> OpenAsync(
        AgentCoreBoot boot,
        AgentCoreConfiguration configuration,
        AgentCoreOptions options,
        CompiledGraph graph,
        VocabularyCache vocabulary,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        IAuditSinkPort store = boot.Track(await AuditSinkFactory
            .OpenAsync(
                configuration,
                options.SecretResolver,
                options.AuditSinks ?? [],
                cancellationToken)
            .ConfigureAwait(false));

        if (store is InMemoryAuditSink && configuration.Providers?.Audit is null)
        {
            StartupLog.AuditSinkDefaulted(loggers.CreateLogger<QueuedAuditSink>());
        }

        QueuedAuditSink auditSink = boot.Track(new QueuedAuditSink(store, loggers.CreateLogger<QueuedAuditSink>()));

        var sessionLogger = loggers.CreateLogger<CallSession>();

        CallSessionFactory sessions = new(
            graph.Compiled,
            graph.Guards,
            CallSessionFactory.CreateExtractor(graph.Compiled, graph.ChatClients, options.StateValueLinkers),
            options.TimeProvider,
            sessionLogger,
            CallObservers.Standard(auditSink, sessionLogger, options.Observers),
            vocabulary);

        return new CallSessionSeam(sessions, new AgentCoreAgent(sessions, configuration.Name), auditSink);
    }
}

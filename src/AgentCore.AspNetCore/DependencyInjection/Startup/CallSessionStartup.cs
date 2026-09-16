using AgentCore.Application.Audit;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Sessions.Memory;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection.Startup;

/// <summary>Everything the call seam opened: the audit chain, and one session factory per entry.</summary>
/// <param name="Entries">One factory, one agent, and one session store per entry, keyed by entry name.</param>
/// <param name="Queue">The queue that answers the audit port, not the store behind it.</param>
internal readonly record struct CallSessionSeam(
    EntryRegistry Entries, QueuedAuditSink Queue);

/// <summary>The seam a call arrives on: the audit queue, the observers, and one session factory per entry.</summary>
internal static class CallSessionStartup
{
    /// <summary>Opens the audit store the document names and builds one session factory per entry over it.</summary>
    /// <param name="boot">The owner the audit chain is tracked against.</param>
    /// <param name="configuration">The loaded document. It carries <c>providers.audit</c>.</param>
    /// <param name="options">The options the host filled. It carries the audit vendors, the clock, and any observer.</param>
    /// <param name="graph">The compiled entries and the seams step 5 made.</param>
    /// <param name="loggers">The factory the session and the audit queue take their loggers from.</param>
    /// <param name="cancellationToken">Cancels the store open.</param>
    /// <returns>The per-entry seams, and the queue in front of the store.</returns>
    internal static async ValueTask<CallSessionSeam> OpenAsync(
        AgentCoreBoot boot,
        AgentCoreConfiguration configuration,
        AgentCoreOptions options,
        CompiledGraph graph,
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

        if (options.WorkspaceRoot is { } root)
        {
            try
            {
                Directory.CreateDirectory(root);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new ConfigurationLoadException(
                    $"The configuration document did not load. The workspace root '{root}' could not be "
                    + $"created: {exception.Message} Check the path passed to options.UseWorkspace(...), "
                    + "and that the process has permission to create it.",
                    exception);
            }
        }

        var observers = CallObservers.Standard(auditSink, sessionLogger, options.Observers);
        var timeProvider = options.TimeProvider ?? TimeProvider.System;

        Dictionary<string, ICallSessionFactory> factories = new(StringComparer.Ordinal);
        Dictionary<string, AgentCoreAgent> agents = new(StringComparer.Ordinal);
        Dictionary<string, ICallSessions> sessions = new(StringComparer.Ordinal);

        foreach (var (entryName, compiled) in graph.Entries)
        {
            CallSessionFactory factory = new(
                compiled,
                graph.Guards,
                CallSessionFactory.CreateExtractor(compiled, graph.ChatClients),
                options.TimeProvider,
                sessionLogger,
                observers,
                options.WorkspaceRoot);

            factories[entryName] = factory;
            agents[entryName] = new AgentCoreAgent(factory, entryName);
            sessions[entryName] = options.CallSessions?.Invoke(entryName, factory)
                ?? new InMemoryCallSessions(
                    factory, InMemoryCallSessions.DefaultIdleTimeout, timeProvider);
        }

        return new CallSessionSeam(new EntryRegistry(factories, agents, sessions), auditSink);
    }
}

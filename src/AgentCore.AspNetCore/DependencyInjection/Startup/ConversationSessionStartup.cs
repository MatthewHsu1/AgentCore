using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Sessions.Memory;
using Microsoft.Extensions.Logging;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.DependencyInjection.Startup
{
    /// <summary>Everything the conversation seam opened: the audit chain, and one session factory per entry.</summary>
    /// <param name="Entries">One factory and one agent per entry, and the one session owner shared by all of them.</param>
    /// <param name="Queue">The queue that answers the audit port, not the store behind it.</param>
    internal readonly record struct ConversationSessionSeam(
        EntryRegistry Entries, QueuedAuditSink Queue);

    /// <summary>The seam a conversation arrives on: the audit queue and one session factory per entry.</summary>
    internal static class ConversationSessionStartup
    {
        /// <summary>Builds the one session owner over every entry's factory.</summary>
        /// <param name="boot">The owner the session owner is tracked against.</param>
        /// <param name="options">The options the host filled. It carries the clock and the workspace.</param>
        /// <param name="graph">The compiled entries and the seams built from them.</param>
        /// <param name="auditSink">The queue that answers the audit port, opened before the compile.</param>
        /// <param name="loggers">The factory the session takes its logger from.</param>
        /// <returns>The per-entry seams, and the queue in front of the store.</returns>
        internal static ConversationSessionSeam Open(
            AgentCoreBoot boot,
            AgentCoreOptions options,
            CompiledGraph graph,
            QueuedAuditSink auditSink,
            ILoggerFactory loggers)
        {
            TimeProvider timeProvider = options.TimeProvider ?? TimeProvider.System;

            ILogger<ConversationSession> sessionLogger = loggers.CreateLogger<ConversationSession>();

            if (options.WorkspaceRoot is { } root)
            {
                try
                {
                    _ = Directory.CreateDirectory(root);
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

            Dictionary<string, IConversationSessionFactory> factories = new(StringComparer.Ordinal);

            foreach ((string? entryName, Application.Configuration.Compilation.CompiledAgent? compiled) in graph.Entries)
            {
                factories[entryName] = new ConversationSessionFactory(
                    compiled,
                    graph.Guards,
                    ConversationSessionFactory.CreateExtractor(compiled, graph.ChatClients),
                    options.TimeProvider,
                    sessionLogger,
                    workspaceRoot: options.WorkspaceRoot);
            }

            IConversationSessions sessions = options.ConversationSessions?.Invoke(factories)
                ?? boot.Track(BuiltinSessions(factories, timeProvider, options.WorkspaceRoot, sessionLogger));

            Dictionary<string, AgentCoreAgent> agents = new(StringComparer.Ordinal);
            foreach (string entryName in factories.Keys)
            {
                agents[entryName] = new AgentCoreAgent(sessions, entryName);
            }

            return new ConversationSessionSeam(new EntryRegistry(factories, agents, sessions), auditSink);
        }

        /// <summary>
        /// Builds the default session owner, and — before anything can open a session through it — sweeps the
        /// workspace root of every folder a crashed process left behind. The sweep runs here, once per boot, in
        /// this one owner, and only for the built-in owner: a host that replaces it with its own implementation
        /// through <c>UseConversationSessions</c> takes over its own workspace cleanup too.
        /// </summary>
        private static InMemoryConversationSessions BuiltinSessions(
            IReadOnlyDictionary<string, IConversationSessionFactory> factories,
            TimeProvider timeProvider,
            string? workspaceRoot,
            ILogger logger)
        {
            InMemoryConversationSessions sessions = new(factories, InMemoryConversationSessions.DefaultIdleTimeout, timeProvider);

            if (workspaceRoot is not null)
            {
                sessions.SweepWorkspaceRoot(workspaceRoot, logger);
            }

            return sessions;
        }
    }
}

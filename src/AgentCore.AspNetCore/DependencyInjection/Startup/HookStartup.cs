using AgentCore.Application.Audit;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.BuiltIn;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.DependencyInjection.Startup
{
    /// <summary>Builds the hook list a compile runs, and raises the notices that belong to the host.</summary>
    internal static class HookStartup
    {
        /// <summary>
        /// Opens the audit store the document names and the queue in front of it, and lists the hooks a compile runs:
        /// AgentCore's built-ins first (the call brief hook only when the host registers a conversation adapter), then
        /// the host's, in registration order. The store and the queue are tracked before the compile's hooks, so a
        /// shutdown drains the hooks first, then the queue, then the store.
        /// </summary>
        /// <param name="boot">The owner the store and the queue are tracked against.</param>
        /// <param name="configuration">The loaded document. It carries <c>providers.audit</c>.</param>
        /// <param name="options">The options the host filled. It carries the audit vendors, the clock, and the hooks.</param>
        /// <param name="loggers">The factory the audit queue takes its logger from.</param>
        /// <param name="services">The container a host hook is resolved from, or <see langword="null"/>.</param>
        /// <param name="cancellationToken">Cancels the store open.</param>
        /// <returns>The hooks, and the queue that answers the audit port.</returns>
        internal static async ValueTask<(IReadOnlyList<AgentHook> Hooks, QueuedAuditSink AuditQueue)> OpenAsync(
            AgentCoreBoot boot,
            AgentCoreConfiguration configuration,
            AgentCoreOptions options,
            ILoggerFactory loggers,
            IServiceProvider? services,
            CancellationToken cancellationToken)
        {
            IAuditSinkPort store = boot.Track(await AuditSinkFactory
                .OpenAsync(configuration, options.SecretResolver, options.AuditSinks ?? [], cancellationToken)
                .ConfigureAwait(false));

            if (store is InMemoryAuditSink && configuration.Providers?.Audit is null)
            {
                StartupLog.AuditSinkDefaulted(loggers.CreateLogger<QueuedAuditSink>());
            }

            ILogger auditLogger = loggers.CreateLogger<QueuedAuditSink>();
            QueuedAuditSink queue = boot.Track(new QueuedAuditSink(store, auditLogger, timeProvider: options.TimeProvider ?? TimeProvider.System));

            // The call brief hook is registered only when the host registers a conversation adapter.
            IReadOnlyList<AgentHook> callHooks = options.ConversationAdapters is null ? [] : [new CallBriefHook()];
            return ([.. BuiltInHooks.Create(queue, loggers.CreateLogger<ConversationSession>()), .. callHooks, .. options.HookFactories.Select(entry => entry.Create(services))], queue);
        }

        /// <summary>Raises a host notice, stamped by the clock the hooks' deadlines run on.</summary>
        /// <param name="runtime">The compile's hooks.</param>
        /// <param name="create">Builds the notice from the host scope.</param>
        /// <returns>The notice's event id.</returns>
        internal static Guid RaiseHostNotice(HookRuntime runtime, Func<HookScope, HookNotice> create)
        {
            return runtime.Notices.Raise(create(HookScopes.Host(runtime.Timers.GetUtcNow())), runtime.Timers);
        }
    }
}

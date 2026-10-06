using AgentCore.Application.Hooks.Notices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Hooks.Engine
{
    /// <summary>The hooks of one compile: which hook overrides what, the gate runner, and the notice hub.</summary>
    internal sealed class HookRuntime : IAsyncDisposable
    {
        /// <summary>How long a stop waits for queued notices when the host names no shutdown timeout.</summary>
        internal static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(30);

        private HookRuntime(HookTable gates, NoticeHub notices, ILogger logger, TimeProvider timers)
        {
            Gates = new GateRunner(gates, logger, timers);
            Notices = notices;
            Logger = logger;
            Timers = timers;
        }

        internal static HookRuntime Empty { get; } = Create([], loggers: null);

        internal HookTable Table => Gates.Table;

        internal GateRunner Gates { get; }

        internal NoticeHub Notices { get; }

        internal ILogger Logger { get; }

        internal TimeProvider Timers { get; }

        /// <summary>Gets or sets how long a dispose waits for queued notices: the host's shutdown timeout.</summary>
        internal TimeSpan StopTimeout { get; set; } = DefaultStopTimeout;

        /// <summary>Builds the runtime of one compile.</summary>
        /// <param name="hooks">The hooks, built-ins first.</param>
        /// <param name="loggers">Where the engine logs, or <see langword="null"/>.</param>
        /// <param name="timers">The clock of the deadlines and timeouts. Production passes nothing: <see cref="TimeProvider.System"/>.</param>
        internal static HookRuntime Create(IReadOnlyList<AgentHook> hooks, ILoggerFactory? loggers, TimeProvider? timers = null)
        {
            HookTable table = HookTable.Build(hooks);
            ILogger logger = loggers?.CreateLogger("AgentCore.Hooks") ?? NullLogger.Instance;
            TimeProvider clock = timers ?? TimeProvider.System;
            return new HookRuntime(table, new NoticeHub(table, logger, clock), logger, clock);
        }

        /// <summary>Builds the runtime of a factory with hooks of its own: the same gates, a notice hub over both sets.</summary>
        /// <exception cref="ArgumentException">A hook overrides a gate and was not compiled in.</exception>
        internal HookRuntime WithNoticeHooks(IEnumerable<AgentHook> hooks)
        {
            ArgumentNullException.ThrowIfNull(hooks);

            AgentHook[] added = [.. hooks];
            foreach (AgentHook hook in added)
            {
                if (hook is null)
                {
                    throw new ArgumentNullException(nameof(hooks), "A hook in the list is null.");
                }

                if (!Table.Hooks.Contains(hook, ReferenceEqualityComparer.Instance) && HookTable.Build([hook]).OverridesAnyGate(hook))
                {
                    throw new ArgumentException(
                        $"The hook '{hook.GetType().FullName}' overrides a gate, and gates are compiled into the agent. "
                        + $"Pass it in {nameof(Configuration.Compilation.AgentCompilationContext)}.{nameof(Configuration.Compilation.AgentCompilationContext.Hooks)}.",
                        nameof(hooks));
                }
            }

            HookTable notices = HookTable.Build([.. Table.Hooks, .. added]);
            return new HookRuntime(Table, new NoticeHub(notices, Logger, Timers), Logger, Timers);
        }

        /// <summary>Raises a gate's <c>Fault(HookFailed)</c> where no session is open (BeforeCall, BeforeEntry).</summary>
        internal void RaiseHostFault(Fault fault, AgentHook except)
        {
            _ = Notices.Raise(fault, Timers, except: except);
        }

        internal ValueTask StopAsync(TimeSpan timeout)
        {
            return Notices.StopAsync(timeout);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return StopAsync(StopTimeout);
        }
    }
}

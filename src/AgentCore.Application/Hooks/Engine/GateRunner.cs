using AgentCore.Application.Hooks.Notices;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Hooks.Engine
{
    /// <summary>Runs one gate's chain: each overriding hook in order, each under the gate's deadline.</summary>
    /// <param name="table">Which hooks override which gate.</param>
    /// <param name="logger">Where a failing hook is reported.</param>
    /// <param name="timers">The clock the deadlines run on.</param>
    internal sealed class GateRunner(HookTable table, ILogger logger, TimeProvider timers)
    {
        internal HookTable Table => table;

        internal bool Overrides(GatePoint point)
        {
            return table.Overrides(point);
        }

        /// <summary>Runs the chain and returns the value the hooks left.</summary>
        /// <typeparam name="TGate">The gate's view type.</typeparam>
        /// <typeparam name="TState">The value the chain decides.</typeparam>
        /// <param name="point">The gate.</param>
        /// <param name="scope">Where and when it fires.</param>
        /// <param name="state">The value before any hook.</param>
        /// <param name="open">Builds one hook's view from the value so far.</param>
        /// <param name="invoke">Calls the hook's gate method.</param>
        /// <param name="fold">Applies one view's staged verbs to the value. Called only for a hook that returned in time, after its view is sealed.</param>
        /// <param name="failClosed">Applies the gate's safe verb, for a hook that fails closed.</param>
        /// <param name="raiseFault">Raises <c>Fault(HookFailed)</c> to every hook but the failing one, or <see langword="null"/>.</param>
        /// <param name="cancellationToken">The turn's token. Its cancellation propagates and is not a hook failure.</param>
        /// <param name="deadline">How long each hook may take, or <see langword="null"/> for <paramref name="point"/>'s own deadline.</param>
        /// <returns>The value after the last hook that ran.</returns>
        internal async ValueTask<TState> RunAsync<TGate, TState>(
            GatePoint point,
            HookScope scope,
            TState state,
            Func<TState, TGate> open,
            Func<AgentHook, TGate, CancellationToken, ValueTask> invoke,
            Func<TState, TGate, TState> fold,
            Func<TState, TState> failClosed,
            Action<Fault, AgentHook>? raiseFault,
            CancellationToken cancellationToken,
            TimeSpan? deadline = null)
            where TGate : HookGate
        {
            TimeSpan limit = deadline ?? point.Deadline;
            foreach (AgentHook hook in table.For(point))
            {
                TGate view = open(state);
                HookRun run;
                try
                {
                    run = await InvokeAsync(hook, view, invoke, limit, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    view.Seal();
                }

                if (run.Failure is null)
                {
                    state = fold(state, view);
                    if (view.IsTerminal)
                    {
                        break;
                    }

                    continue;
                }

                Report(point, hook, scope, run, limit, raiseFault);

                if (FailureOf(hook, point) == HookFailure.Closed)
                {
                    return failClosed(state);
                }
            }

            return state;
        }

        private static HookFailure FailureOf(AgentHook hook, GatePoint point)
        {
            try
            {
                return hook.FailureFor(point);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return point.DefaultFailure;
            }
        }

        // A hook's own TimeoutException faulted its task; the wait's own timeout is a fresh exception.
        private static bool ThrownBy(Task running, Exception exception)
        {
            return running.Exception?.InnerExceptions.Contains(exception) == true;
        }

        // Disposing a source unhooks it from the turn token and stops its timer, so a hook still running would
        // never see its token cancelled. The sources live until the hook ends, however long that is.
        private static void ReleaseWhenDone(Task running, CancellationTokenSource deadline, CancellationTokenSource linked)
        {
            if (running.IsCompleted)
            {
                linked.Dispose();
                deadline.Dispose();
                return;
            }

            _ = running.ContinueWith(
                task =>
                {
                    _ = task.Exception;
                    linked.Dispose();
                    deadline.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async ValueTask<HookRun> InvokeAsync<TGate>(
            AgentHook hook,
            TGate view,
            Func<AgentHook, TGate, CancellationToken, ValueTask> invoke,
            TimeSpan limit,
            CancellationToken cancellationToken)
        {
            CancellationTokenSource deadline = new(limit, timers);
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

            // The hook starts on the pool, so one that blocks its thread before its first await still
            // misses the deadline instead of holding the turn.
            Task running = Task.Run(() => invoke(hook, view, linked.Token).AsTask(), CancellationToken.None);
            try
            {
                await running.WaitAsync(limit, timers, cancellationToken).ConfigureAwait(false);
                return HookRun.Completed;
            }
            catch (TimeoutException exception) when (!ThrownBy(running, exception))
            {
                return new HookRun(exception, Abandoned: true);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                return new HookRun(exception, Abandoned: false);
            }
            finally
            {
                ReleaseWhenDone(running, deadline, linked);
            }
        }

        private void Report(GatePoint point, AgentHook hook, HookScope scope, HookRun run, TimeSpan limit, Action<Fault, AgentHook>? raiseFault)
        {
            Exception failure = run.Failure!;
            string hookType = hook.GetType().FullName ?? hook.GetType().Name;
            string conversationId = scope.ConversationId ?? string.Empty;

            if (run.Abandoned)
            {
                HookLog.GateAbandoned(logger, hookType, point.Name, conversationId, (long)limit.TotalMilliseconds);
            }
            else
            {
                HookLog.GateFailed(logger, hookType, point.Name, conversationId, failure);
            }

            raiseFault?.Invoke(
                new Fault(scope, FaultKind.HookFailed, $"{hookType} failed at gate {point.Name}: {failure.GetType().Name}: {failure.Message}", failure),
                hook);
        }

        /// <summary>How one hook's call ended.</summary>
        /// <param name="Failure">What went wrong, or <see langword="null"/> when the hook returned in time.</param>
        /// <param name="Abandoned">Whether the hook missed the deadline, rather than threw in time.</param>
        private readonly record struct HookRun(Exception? Failure, bool Abandoned)
        {
            public static HookRun Completed => default;
        }
    }
}

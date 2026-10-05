using System.Threading.Channels;
using AgentCore.Application.Hooks.Notices;

namespace AgentCore.Application.Hooks.Engine
{
    /// <summary>
    /// One hook's queue inside one mailbox, and the task that reads it. Notices reach the hook one at a time, in
    /// the order the mailbox wrote them.
    /// </summary>
    internal sealed class HookReader
    {
        /// <summary>The longest delay a timer takes: <c>uint.MaxValue - 1</c> ms, about 49.7 days.</summary>
        internal static readonly TimeSpan LongestTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        private readonly Channel<Delivery> _channel = Channel.CreateUnbounded<Delivery>(new UnboundedChannelOptions { SingleReader = true });

        private readonly Mailbox _box;

        private readonly IReadOnlySet<Type> _wants;

        private readonly TimeSpan? _timeout;

        private Task? _reading;

        private int _pending;

        internal HookReader(Mailbox box, AgentHook hook, IReadOnlySet<Type> wants)
        {
            _box = box;
            Hook = hook;
            _wants = wants;
            _timeout = TimeoutOf(hook);
        }

        internal AgentHook Hook { get; }

        internal Task Completion => Volatile.Read(ref _reading) ?? Task.CompletedTask;

        /// <summary>Gets whether a notice was ever written here, which starts the reading task.</summary>
        internal bool Started => Volatile.Read(ref _reading) is not null;

        internal bool IsIdle => Volatile.Read(ref _pending) == 0;

        internal bool Wants(HookNotice notice) => _wants.Contains(notice.GetType());

        /// <summary>Queues one notice. Runs under the mailbox lock, so queue order is <c>Sequence</c> order.</summary>
        internal void Write(HookNotice notice)
        {
            _ = Interlocked.Increment(ref _pending);
            if (!_channel.Writer.TryWrite(new Delivery(notice, null)))
            {
                _ = Interlocked.Decrement(ref _pending);
                return;
            }

            if (_reading is null)
            {
                Volatile.Write(ref _reading, StartReading());
            }
        }

        /// <summary>Queues a barrier. Runs under the mailbox lock. Completes once every earlier notice was handled.</summary>
        internal Task Barrier()
        {
            if (_reading is null)
            {
                return Task.CompletedTask;
            }

            TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Interlocked.Increment(ref _pending);
            if (_channel.Writer.TryWrite(new Delivery(null, reached)))
            {
                return reached.Task;
            }

            _ = Interlocked.Decrement(ref _pending);
            return Task.CompletedTask;
        }

        internal void Complete()
        {
            _ = _channel.Writer.TryComplete();
        }

        // A timer refuses a negative delay or one past LongestTimeout. Longer than a timer can wait means
        // "wait for every notice", which is what null says; a negative one is a mistake and gets the default.
        private static TimeSpan? TimeoutOf(AgentHook hook)
        {
            TimeSpan? timeout;
            try
            {
                timeout = hook.NoticeTimeout;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return DefaultTimeout;
            }

            return timeout switch
            {
                null => null,
                { } value when value == Timeout.InfiniteTimeSpan || value > LongestTimeout => null,
                { } value when value < TimeSpan.Zero => DefaultTimeout,
                _ => timeout,
            };
        }

        private static string NameOf(AgentHook hook) => hook.GetType().FullName ?? hook.GetType().Name;

        // The reader lives as long as the mailbox. Started with the raiser's context, every later delivery would
        // run inside the first turn's trace, logging scopes and run context, and keep them alive.
        private Task StartReading()
        {
            bool restoreFlow = false;
            try
            {
                if (!ExecutionContext.IsFlowSuppressed())
                {
                    _ = ExecutionContext.SuppressFlow();
                    restoreFlow = true;
                }

                return Task.Run(ReadAsync);
            }
            finally
            {
                if (restoreFlow)
                {
                    ExecutionContext.RestoreFlow();
                }
            }
        }

        private async Task ReadAsync()
        {
            ChannelReader<Delivery> reader = _channel.Reader;
            while (true)
            {
                bool more;
                using (CancellationTokenSource idle = new(NoticeHub.IdleAfter, _box.Hub.Timers))
                {
                    try
                    {
                        more = await reader.WaitToReadAsync(idle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (idle.IsCancellationRequested)
                    {
                        // An evicted mailbox completes this channel, so the next wait returns false and the loop ends.
                        _ = _box.TryEvict();
                        continue;
                    }
                }

                if (!more)
                {
                    return;
                }

                while (reader.TryRead(out Delivery delivery))
                {
                    await HandleAsync(delivery).ConfigureAwait(false);
                }
            }
        }

        // A reader that died would leave its channel growing, its mailbox never evicted, and every flush waiting.
        private async Task HandleAsync(Delivery delivery)
        {
            try
            {
                if (delivery.Barrier is { } barrier)
                {
                    _ = barrier.TrySetResult();
                }
                else
                {
                    await DeliverAsync(delivery.Notice!).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                EngineFailed(delivery.Notice, exception);
            }
            finally
            {
                _ = Interlocked.Decrement(ref _pending);
            }
        }

        private async Task DeliverAsync(HookNotice notice)
        {
            using CancellationTokenSource deadline = _timeout is { } limit ? new(limit, _box.Hub.Timers) : new();

            Task running;
            try
            {
                running = NoticeDispatch.DeliverAsync(Hook, notice, deadline.Token).AsTask();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Failed(notice, exception);
                return;
            }

            if (_timeout is { } timeout)
            {
                await WithinAsync(notice, running, timeout, deadline).ConfigureAwait(false);
            }
            else
            {
                await PatientlyAsync(notice, running).ConfigureAwait(false);
            }
        }

        private async Task WithinAsync(HookNotice notice, Task running, TimeSpan timeout, CancellationTokenSource deadline)
        {
            try
            {
                await running.WaitAsync(timeout, _box.Hub.Timers).ConfigureAwait(false);
            }
            // A hook that honours its token gives up at the deadline too; that is the same missed deadline.
            catch (Exception exception) when ((exception is TimeoutException && !running.IsCompleted)
                || (exception is OperationCanceledException && deadline.IsCancellationRequested))
            {
                HookLog.NoticeAbandoned(_box.Hub.Logger, NameOf(Hook), notice.GetType().Name, notice.Scope.ConversationId ?? string.Empty, (long)timeout.TotalMilliseconds);
                _ = running.ContinueWith(static task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                RaiseFault(notice, $"{NameOf(Hook)} did not finish {notice.GetType().Name} within {timeout.TotalMilliseconds:0} ms.", cause: null);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Failed(notice, exception);
            }
        }

        private async Task PatientlyAsync(HookNotice notice, Task running)
        {
            bool reported = false;
            while (true)
            {
                try
                {
                    await running.WaitAsync(NoticeHub.StillWaitingEvery, _box.Hub.Timers).ConfigureAwait(false);
                    return;
                }
                catch (TimeoutException) when (!running.IsCompleted)
                {
                    HookLog.NoticeStillWaiting(_box.Hub.Logger, NameOf(Hook), notice.GetType().Name, notice.Scope.ConversationId ?? string.Empty);
                    if (!reported)
                    {
                        reported = true;
                        RaiseFault(notice, $"{NameOf(Hook)} is still working on {notice.GetType().Name}.", cause: null);
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Failed(notice, exception);
                    return;
                }
            }
        }

        private void EngineFailed(HookNotice? notice, Exception exception)
        {
            try
            {
                HookLog.DeliveryFailed(_box.Hub.Logger, NameOf(Hook), notice?.GetType().Name ?? string.Empty, notice?.Scope.ConversationId ?? string.Empty, exception);
            }
            catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
            {
                // The logger itself throws. Nothing is left to report to, and the reader must go on.
                _ = logFailure;
            }
        }

        private void Failed(HookNotice notice, Exception exception)
        {
            HookLog.NoticeFailed(_box.Hub.Logger, NameOf(Hook), notice.GetType().Name, notice.Scope.ConversationId ?? string.Empty, exception);
            RaiseFault(notice, $"{NameOf(Hook)} failed on {notice.GetType().Name}: {exception.GetType().Name}: {exception.Message}", exception);
        }

        private void RaiseFault(HookNotice notice, string message, Exception? cause)
        {
            // Two hooks that both throw on faults would otherwise trade HookFailed notices forever.
            if (notice is Fault { Kind: FaultKind.HookFailed })
            {
                return;
            }

            _ = _box.Hub.Raise(new Fault(notice.Scope, FaultKind.HookFailed, message, cause), _box.Hub.Timers, except: Hook);
        }
    }
}

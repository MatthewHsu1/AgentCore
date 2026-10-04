
using AgentCore.AspNetCore.Voice.Ports;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>An output port that records what it was told to say, and when it was stopped.</summary>
    internal sealed class FakeConversationOutput : IConversationOutputPort
    {
        private readonly List<string> _spoken = [];
        private readonly List<string> _log = [];
        private readonly List<(int Count, TaskCompletionSource Reached)> _logWaiters = [];
        private readonly Lock _gate = new();
        private int _completions;
        private int _stops;

        /// <summary>Gets every fragment this port was given, in order.</summary>
        public IReadOnlyList<string> Spoken
        {
            get { lock (_gate) { return [.. _spoken]; } }
        }

        /// <summary>Gets every call but <see cref="BeginReply"/> in order: <c>speak:</c> and the fragment, <c>complete</c>, or <c>stop</c>.</summary>
        public IReadOnlyList<string> Log
        {
            get { lock (_gate) { return [.. _log]; } }
        }

        /// <summary>Gets how many replies were closed.</summary>
        public int Completions => Volatile.Read(ref _completions);

        /// <summary>Gets how many times a barge-in stopped this port.</summary>
        public int Stops => Volatile.Read(ref _stops);

        /// <summary>Gets how many times this port was disposed.</summary>
        public int Disposals { get; private set; }

        /// <summary>Gets or sets what <see cref="SpeakAsync"/> waits on after it logged a fragment, or null for nothing.</summary>
        public Func<string, CancellationToken, Task>? BeforeSpeakReturns { get; set; }

        /// <summary>Waits until <see cref="Log"/> holds at least <paramref name="count"/> entries.</summary>
        /// <param name="count">How many entries to wait for.</param>
        /// <returns>A task that completes once they are there.</returns>
        public Task WaitForLogAsync(int count)
        {
            lock (_gate)
            {
                if (_log.Count >= count)
                {
                    return Task.CompletedTask;
                }

                TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _logWaiters.Add((count, reached));
                return reached.Task;
            }
        }

        public void BeginReply()
        {
        }

        public ValueTask SpeakAsync(string fragment, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _spoken.Add(fragment);
                AppendLocked("speak:" + fragment);
            }

            return BeforeSpeakReturns is { } wait ? new ValueTask(wait(fragment, cancellationToken)) : ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _completions++;
                AppendLocked("complete");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _stops++;
                AppendLocked("stop");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }

        private void AppendLocked(string entry)
        {
            _log.Add(entry);
            _ = _logWaiters.RemoveAll(waiter => waiter.Count <= _log.Count && waiter.Reached.TrySetResult());
        }
    }
}

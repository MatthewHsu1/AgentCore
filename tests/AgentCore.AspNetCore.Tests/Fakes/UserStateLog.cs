using AgentCore.AspNetCore.Voice;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>Every user state change a <see cref="VoiceSession"/> raises, in order.</summary>
    internal sealed class UserStateLog
    {
        private readonly Lock _gate = new();
        private readonly List<UserStateChanged> _changes = [];
        private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];

        public UserStateLog(VoiceSession session)
        {
            session.UserStateChanged += change =>
            {
                lock (_gate)
                {
                    _changes.Add(change);
                    _ = _waiters.RemoveAll(waiter => waiter.Count <= _changes.Count && waiter.Reached.TrySetResult());
                }
            };
        }

        /// <summary>Gets each change's new state, in order.</summary>
        public IReadOnlyList<UserState> NewStates
        {
            get { lock (_gate) { return [.. _changes.Select(change => change.NewState)]; } }
        }

        /// <summary>Waits until at least <paramref name="count"/> changes were raised.</summary>
        public Task WaitForCountAsync(int count)
        {
            lock (_gate)
            {
                if (_changes.Count >= count)
                {
                    return Task.CompletedTask;
                }

                TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, reached));
                return reached.Task;
            }
        }
    }
}

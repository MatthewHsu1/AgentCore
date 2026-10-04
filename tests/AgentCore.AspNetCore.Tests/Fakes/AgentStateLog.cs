
using AgentCore.AspNetCore.Voice.Session;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>Every agent state change a <see cref="VoiceSession"/> raises, in order.</summary>
    internal sealed class AgentStateLog
    {
        private readonly Lock _gate = new();
        private readonly List<AgentStateChanged> _changes = [];
        private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];

        public AgentStateLog(VoiceSession session)
        {
            session.AgentStateChanged += change =>
            {
                lock (_gate)
                {
                    _changes.Add(change);
                    _ = _waiters.RemoveAll(waiter => waiter.Count <= _changes.Count && waiter.Reached.TrySetResult());
                }
            };
        }

        /// <summary>Gets each change's new state, in order.</summary>
        public IReadOnlyList<AgentState> NewStates
        {
            get { lock (_gate) { return [.. _changes.Select(change => change.NewState)]; } }
        }

        /// <summary>Gets each change as an old and a new state, in order.</summary>
        public IReadOnlyList<(AgentState Old, AgentState New)> Transitions
        {
            get { lock (_gate) { return [.. _changes.Select(change => (change.OldState, change.NewState))]; } }
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

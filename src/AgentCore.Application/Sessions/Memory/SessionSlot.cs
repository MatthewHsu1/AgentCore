using System.Diagnostics.CodeAnalysis;

namespace AgentCore.Application.Sessions.Memory
{
    /// <summary>
    /// The place one conversation id takes in <see cref="InMemoryConversationSessions"/>. An open reserves it
    /// before the session is built, and it is given up only once the build failed or the session's teardown is
    /// done, so no second session of the id can start while the first is still being built or torn down.
    /// </summary>
    /// <param name="entry">The entry that reserved the id.</param>
    internal sealed class SessionSlot(string entry)
    {
        private readonly TaskCompletionSource<HeldSession> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private volatile HeldSession? _held;

        /// <summary>Gets the entry that reserved the id.</summary>
        internal string Entry { get; } = entry;

        /// <summary>Gets a task that completes with the held session once it is built, or faults with what the build threw.</summary>
        internal Task<HeldSession> Opened => _opened.Task;

        /// <summary>
        /// Gets a task that completes once this slot is out of the dictionary: its build failed, or its session's
        /// teardown is done.
        /// </summary>
        internal Task Released => _released.Task;

        /// <summary>Reads the held session, once it is built.</summary>
        internal bool TryGetHeld([NotNullWhen(true)] out HeldSession? held)
        {
            held = _held;
            return held is not null;
        }

        /// <summary>Hands the built session to every caller waiting on <see cref="Opened"/>.</summary>
        internal void Open(HeldSession held)
        {
            _held = held;
            _opened.SetResult(held);
        }

        /// <summary>Hands the build's fault to every caller waiting on <see cref="Opened"/>. Call it once the slot is out of the dictionary.</summary>
        internal void Fail(Exception fault)
        {
            _opened.SetException(fault);

            // The builder rethrows the fault itself, so a slot nobody else waited on must not report it again as unobserved.
            _ = _opened.Task.Exception;
            _released.SetResult();
        }

        /// <summary>Wakes every caller waiting for the teardown. Call it once the slot is out of the dictionary.</summary>
        internal void Release()
        {
            _released.SetResult();
        }
    }
}

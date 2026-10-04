using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// Passes every call to the sessions it wraps. Once <see cref="Arm"/>ed, the next open parks until
    /// <see cref="Release"/> or its caller's cancellation; later opens pass straight through. After
    /// <see cref="Fault"/>, the next open throws instead. After <see cref="HoldTryGet"/>, one chosen lookup parks
    /// until <see cref="ReleaseTryGet"/>. After <see cref="FaultTryGets"/>, every lookup throws.
    /// </summary>
    internal sealed class GatedConversationSessions(IConversationSessions inner) : IConversationSessions
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _tryGetGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _armed;

        private Exception? _fault;

        private int _heldTryGet;

        private Exception? _tryGetFault;

        private int _tryGets;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task that completes once the held lookup reached this store.</summary>
        public TaskCompletionSource TryGetHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _gate.TrySetResult();

        public void Fault(Exception fault) => Volatile.Write(ref _fault, fault);

        /// <summary>Parks the <paramref name="ordinal"/>-th lookup from now on, counting from 1.</summary>
        public void HoldTryGet(int ordinal) => Volatile.Write(ref _heldTryGet, Volatile.Read(ref _tryGets) + ordinal);

        public void ReleaseTryGet() => _tryGetGate.TrySetResult();

        public void FaultTryGets(Exception fault) => Volatile.Write(ref _tryGetFault, fault);

        public async ValueTask<ConversationSession> GetOrOpenAsync(
            string entry, string? conversationId, ConversationSessionState? state, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fault, null) is { } fault)
            {
                throw fault;
            }

            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _ = Entered.TrySetResult();
                await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await inner.GetOrOpenAsync(entry, conversationId, state, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<ConversationSession?> TryGetAsync(string entry, string conversationId, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _tryGetFault) is { } fault)
            {
                throw fault;
            }

            if (Interlocked.Increment(ref _tryGets) == Volatile.Read(ref _heldTryGet))
            {
                _ = TryGetHeld.TrySetResult();
                await _tryGetGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await inner.TryGetAsync(entry, conversationId, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask CloseAsync(string entry, string conversationId, CancellationToken cancellationToken = default) =>
            inner.CloseAsync(entry, conversationId, cancellationToken);
    }
}

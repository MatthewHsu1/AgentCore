using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;

namespace AgentCore.Application.Tests.Sessions
{
    /// <summary>
    /// Stalls inside <see cref="Create"/> until the test releases it, so a test can hold one build open while
    /// other callers race it.
    /// </summary>
    internal sealed class GatedSessionFactory(IConversationSessionFactory inner) : IConversationSessionFactory, IDisposable
    {
        private readonly ManualResetEventSlim _released = new();

        private int _calls;

        /// <summary>Gets the signal set once <see cref="Create"/> has been entered.</summary>
        internal ManualResetEventSlim Entered { get; } = new();

        /// <summary>Gets the session <see cref="Create"/> built, or <see langword="null"/> before it returned.</summary>
        internal ConversationSession? Built { get; private set; }

        /// <summary>Gets how many times <see cref="Create"/> was entered.</summary>
        internal int Calls => Volatile.Read(ref _calls);

        /// <summary>Gets what the first <see cref="Create"/> throws once released, instead of building.</summary>
        internal Exception? FirstBuildFault { get; init; }

        internal void Release()
        {
            _released.Set();
        }

        public ConversationSession Create(string? conversationId = null, ConversationSessionState? state = null)
        {
            int call = Interlocked.Increment(ref _calls);
            Entered.Set();
            _ = _released.Wait(TimeSpan.FromSeconds(10));

            if (call == 1 && FirstBuildFault is { } fault)
            {
                throw fault;
            }

            Built = inner.Create(conversationId, state);
            return Built;
        }

        public void Dispose()
        {
            _released.Dispose();
            Entered.Dispose();
        }
    }
}

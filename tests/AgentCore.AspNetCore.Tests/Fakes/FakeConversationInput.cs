using System.Runtime.CompilerServices;
using AgentCore.AspNetCore.Voice.Ports;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>An input port that replays a scripted stream, refuses a second reader, and counts its disposals.</summary>
    /// <param name="script">What the caller does, in order. Empty is a conversation the caller never spoke on.</param>
    internal sealed class FakeConversationInput(params ConversationInput[] script) : IConversationInputPort
    {
        private int _listening;

        /// <summary>Gets how many times this port was disposed.</summary>
        public int Disposals { get; private set; }

        /// <inheritdoc />
        public IAsyncEnumerable<ConversationInput> ListenAsync(CancellationToken cancellationToken = default)
        {
            // Thrown from here, not from the iterator below: an iterator's body does not run until
            // something enumerates it, so a guard inside it would let a second ListenAsync return a
            // stream that only throws later, or never, if nobody enumerates it.
            return Interlocked.CompareExchange(ref _listening, 1, 0) != 0
                ? throw new InvalidOperationException("This port is already being read.")
                : ReplayAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }

        /// <summary>Replays the script, one item at a time.</summary>
        /// <param name="cancellationToken">Ends the stream.</param>
        /// <returns>Every scripted item, in order.</returns>
        private async IAsyncEnumerable<ConversationInput> ReplayAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (ConversationInput input in script)
            {
                // Yielded from a continuation rather than inline, so a consumer of this fake meets the
                // same interleaving a real socket gives it and never a stream that completes
                // synchronously inside its own await foreach.
                await Task.Yield();

                // Throws rather than breaking out of the loop: a cancelled read is not the end of a
                // conversation, and this fake must not be the one place a consumer learns otherwise.
                cancellationToken.ThrowIfCancellationRequested();

                yield return input;
            }
        }
    }
}

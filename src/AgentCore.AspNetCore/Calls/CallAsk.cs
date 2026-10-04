using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Calls
{
    /// <summary>One ask that has no answer sent yet.</summary>
    internal sealed class CallAsk(string words, IReadOnlyList<ChatMessage> before, CancellationTokenSource stop)
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the words this ask's turn carries.</summary>
        internal string Words => words;

        /// <summary>Gets what was said ahead of <see cref="Words"/> that no turn answered, which the turn writes before them.</summary>
        internal IReadOnlyList<ChatMessage> Before => before;

        internal CancellationToken Token => stop.Token;

        /// <summary>Cancels the ask, and waits up to <paramref name="wait"/> for its turn to seal.</summary>
        /// <returns><see langword="false"/> when the turn was still running after the wait.</returns>
        internal async Task<bool> StopAsync(TimeSpan wait, TimeProvider time)
        {
            try
            {
                await stop.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The ask already finished; there is nothing left to cancel.
            }

            try
            {
                await _done.Task.WaitAsync(wait, time).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        internal void Finish()
        {
            _ = _done.TrySetResult();
            stop.Dispose();
        }
    }
}

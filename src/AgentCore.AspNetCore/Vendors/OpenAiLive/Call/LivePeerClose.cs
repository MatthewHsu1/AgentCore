using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// Waits for the peer to hang the AI leg up after a refer, the only sign the line took the call (see
    /// <see cref="LiveReferTransfer"/>). The caller's words are still heard meanwhile, but start no turn.
    /// </summary>
    internal sealed class LivePeerClose(ILiveSideband sideband, LiveHearing hearing, TimeProvider time)
    {
        /// <summary>Reads the sideband until the peer closes it or <paramref name="wait"/> runs out.</summary>
        /// <returns>Whether the peer closed it, and the receive still open when it did not.</returns>
        internal async Task<(bool Closed, Task<string?>? Receiving)> WaitAsync(TimeSpan wait, Task<string?>? receiving, CancellationToken cancellationToken)
        {
            using CancellationTokenSource waitStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task waited = Task.Delay(wait, time, waitStop.Token);
            try
            {
                while (true)
                {
                    receiving ??= sideband.ReceiveAsync(cancellationToken).AsTask();
                    if (await Task.WhenAny(receiving, waited).ConfigureAwait(false) == waited)
                    {
                        await hearing.FlushAsync().ConfigureAwait(false);
                        return (false, receiving);
                    }

                    string? json = await receiving.ConfigureAwait(false);
                    receiving = null;
                    LiveEvent? liveEvent = json is null ? null : LiveEventReader.Read(json);

                    if (liveEvent is LiveEvent.Transcript delta)
                    {
                        await hearing.HearAsync(delta).ConfigureAwait(false);
                    }
                    else if (json is null || liveEvent is LiveEvent.Closed)
                    {
                        await hearing.FlushAsync().ConfigureAwait(false);
                        return (true, null);
                    }
                }
            }
            finally
            {
                await waitStop.CancelAsync().ConfigureAwait(false);
            }
        }
    }
}

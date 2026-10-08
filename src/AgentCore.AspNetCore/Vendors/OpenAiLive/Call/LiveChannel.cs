using AgentCore.Application.Conversation.Commands;
using AgentCore.Application.Runtime.Session;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// GPT-Live's channel. The webhook attaches it as soon as the call is admitted, before <c>CallStarted</c>, so a host
    /// can send a command from that notice on. A transfer is the <see cref="LiveTransfer"/>'s, once the call runs. Voice
    /// context goes out as silent context at once, which in a probe never made the voice speak or cut its speech
    /// (docs/probes/live-late-brief); context sent before <c>session.started</c> waits for it, as OpenAI's guide sends
    /// nothing before it. A plan goes out the same way, numbered, so a newer plan replaces the older ones.
    /// </summary>
    internal sealed class LiveChannel(string callId, ILogger logger) : IConversationChannel
    {
        private readonly Lock _gate = new();

        private readonly List<string> _waiting = [];

        private int _plans;

        // The last send in line. Each send waits for the one before it, so context and plans reach GPT-Live in the
        // order they were numbered and queued, even when one is sent while earlier ones are still going out.
        private Task _lastSend = Task.CompletedTask;

        private LiveTransfer? _transfer;

        private Func<string, CancellationToken, ValueTask>? _tell;

        private CancellationToken _stopping;

        public ChannelCommandResult Send(ChannelCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            return command switch
            {
                TransferCommand transfer => Volatile.Read(ref _transfer)?.Send(transfer) ?? ChannelCommandResult.NotSupported,
                AddVoiceContextCommand context => Add(context.Text, numbered: false),
                SetVoicePlanCommand plan => Add(plan.Text, numbered: true),
                _ => ChannelCommandResult.NotSupported,
            };
        }

        /// <summary>Lets the call transfer. A transfer sent before this answers as not supported.</summary>
        internal void Transfers(LiveTransfer transfer)
        {
            Volatile.Write(ref _transfer, transfer);
        }

        /// <summary>On <c>session.started</c>, before the greeting: tells the context that waited, then each later one at once.</summary>
        /// <param name="tell">Sends one piece of context to GPT-Live.</param>
        /// <param name="cancellationToken">The call's token. Later context goes out under it too.</param>
        internal async ValueTask OpenAsync(Func<string, CancellationToken, ValueTask> tell, CancellationToken cancellationToken)
        {
            List<(string Text, Task Previous, TaskCompletionSource Done)> waiting = [];

            lock (_gate)
            {
                _stopping = cancellationToken;

                _tell = tell;

                foreach (string text in _waiting)
                {
                    (Task previous, TaskCompletionSource done) = TakePlaceInLine();
                    waiting.Add((text, previous, done));
                }

                _waiting.Clear();
            }

            await Task.WhenAll(waiting.Select(item => SendInTurnAsync(item.Previous, item.Done, () => tell(item.Text, cancellationToken).AsTask())))
                .ConfigureAwait(false);
        }

        private ChannelCommandResult Add(string text, bool numbered)
        {
            Func<string, CancellationToken, ValueTask>? tell;

            Task previous;

            TaskCompletionSource done;

            lock (_gate)
            {
                if (numbered)
                {
                    // GPT-Live cannot delete an append; this line is what makes a newer plan replace the older ones.
                    text = $"Plan {++_plans}. This replaces every earlier plan.\n{text}";
                }

                tell = _tell;

                if (tell is null)
                {
                    _waiting.Add(text);
                    return ChannelCommandResult.Scheduled;
                }

                (previous, done) = TakePlaceInLine();
            }

            _ = SendInTurnAsync(previous, done, () => TellAsync(tell, text));

            return ChannelCommandResult.Scheduled;
        }

        // Call under _gate. The send itself runs outside the lock.
        private (Task Previous, TaskCompletionSource Done) TakePlaceInLine()
        {
            TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Task previous = _lastSend;

            _lastSend = done.Task;

            return (previous, done);
        }

        // A place in line only ever completes, never faults, so a failed send never stops the ones after it.
        private static async Task SendInTurnAsync(Task previous, TaskCompletionSource done, Func<Task> send)
        {
            try
            {
                await previous.ConfigureAwait(false);
                await send().ConfigureAwait(false);
            }
            finally
            {
                done.SetResult();
            }
        }

        private async Task TellAsync(Func<string, CancellationToken, ValueTask> tell, string text)
        {
            try
            {
                await tell(text, _stopping).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The call ended and let go of its socket: there is no voice left to tell.
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // The host is stopping; OpenAiLiveCalls ends the call itself.
            }
            catch (Exception fault) when (fault is not OutOfMemoryException)
            {
                OpenAiLiveLog.VoiceContextFailed(logger, callId, fault);
            }
        }
    }
}

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
    /// nothing before it.
    /// </summary>
    internal sealed class LiveChannel(string callId, ILogger logger) : IConversationChannel
    {
        private readonly Lock _gate = new();

        private readonly List<string> _waiting = [];

        private LiveTransfer? _transfer;

        private Func<string, CancellationToken, ValueTask>? _tell;

        private CancellationToken _stopping;

        public ChannelCommandResult Send(ChannelCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            return command switch
            {
                TransferCommand transfer => Volatile.Read(ref _transfer)?.Send(transfer) ?? ChannelCommandResult.NotSupported,
                AddVoiceContextCommand context => Add(context.Text),
                _ => ChannelCommandResult.NotSupported,
            };
        }

        /// <summary>Lets the call transfer. A transfer sent before this answers as not supported.</summary>
        internal void Transfers(LiveTransfer transfer) => Volatile.Write(ref _transfer, transfer);

        /// <summary>On <c>session.started</c>, before the greeting: tells the context that waited, then each later one at once.</summary>
        /// <param name="tell">Sends one piece of context to GPT-Live.</param>
        /// <param name="cancellationToken">The call's token. Later context goes out under it too.</param>
        internal async ValueTask OpenAsync(Func<string, CancellationToken, ValueTask> tell, CancellationToken cancellationToken)
        {
            string[] waiting;
            lock (_gate)
            {
                _stopping = cancellationToken;
                _tell = tell;
                waiting = [.. _waiting];
                _waiting.Clear();
            }

            foreach (string text in waiting)
            {
                await tell(text, cancellationToken).ConfigureAwait(false);
            }
        }

        private ChannelCommandResult Add(string text)
        {
            Func<string, CancellationToken, ValueTask>? tell;
            lock (_gate)
            {
                tell = _tell;
                if (tell is null)
                {
                    _waiting.Add(text);
                    return ChannelCommandResult.Scheduled;
                }
            }

            _ = TellAsync(tell, text);
            return ChannelCommandResult.Scheduled;
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

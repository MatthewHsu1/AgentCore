using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AgentCore.AspNetCore.Vendors.TelnyxRelay.Wire;
using AgentCore.AspNetCore.Voice.Ports;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay.Connection
{
    /// <summary>
    /// The caller half of one relay socket: turns each inbound frame into a <see cref="ConversationInput"/>.
    /// </summary>
    /// <param name="logger">The logger of the connection.</param>
    /// <param name="conversationId">Reads the id of the conversation for a log line, or a placeholder before it started.</param>
    internal sealed class TelnyxRelayInput(ILogger logger, Func<string> conversationId)
        : IConversationInputPort
    {
        private readonly Channel<(ConversationInput Input, TaskCompletionSource Handled)> _inputs =
            Channel.CreateUnbounded<(ConversationInput Input, TaskCompletionSource Handled)>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        private int _listening;

        private bool _loggedMalformedInterrupt;

        private bool _sawSetup;

        /// <summary>Reads one frame the relay sent, in the order it sent them.</summary>
        /// <param name="frame">The frame the reader parsed.</param>
        /// <returns>
        /// A task that completes once the consumer has handled the event the frame became, or at once when
        /// it became none. The read loop awaits it, so a close frame cannot overtake the setup before it.
        /// </returns>
        public Task AcceptAsync(RelayFrame frame)
        {
            switch (frame)
            {
                case RelayFrame.Setup setup:
                    _sawSetup = true;
                    return PublishAsync(new ConversationInput.Started(setup.ConversationSessionId)
                    {
                        From = setup.From,
                        To = setup.To,
                        Headers = setup.CustomParameters,
                    });

                case RelayFrame.Prompt prompt:
                    return PublishAsync(new ConversationInput.Utterance(prompt.VoicePrompt, prompt.Lang, prompt.Last));

                case RelayFrame.Interrupt interrupt:
                    return AcceptInterruptAsync(interrupt);

                case RelayFrame.Dtmf dtmf:
                    // Never the digit itself. A keypad carries card numbers, PINs, and dates of birth,
                    // and the house rule in Log.cs is that no line carries what the caller said.
                    TelnyxRelayLog.DtmfReceived(logger);
                    return PublishAsync(new ConversationInput.Keypress(dtmf.Digit));

                case RelayFrame.Error error:
                    // The vendor refused a frame this endpoint sent. That is our defect.
                    TelnyxRelayLog.FrameRefused(logger, conversationId(), error.Description);
                    return Task.CompletedTask;

                default:
                    // The reader hands back only the frames this build models; see TelnyxRelayFrameReader.TryRead.
                    return Task.CompletedTask;
            }
        }

        /// <summary>Ends the stream: the socket is gone, so the conversation is over.</summary>
        public void Complete()
        {
            _ = _inputs.Writer.TryComplete();
        }

        /// <inheritdoc />
        public IAsyncEnumerable<ConversationInput> ListenAsync(CancellationToken cancellationToken = default)
        {
            return Interlocked.CompareExchange(ref _listening, 1, 0) != 0
                ? throw new InvalidOperationException("This relay connection is already being read.")
                : ListenCoreAsync(cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        private async IAsyncEnumerable<ConversationInput> ListenCoreAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await foreach ((ConversationInput input, TaskCompletionSource handled) in _inputs.Reader
                    .ReadAllAsync(cancellationToken)
                    .ConfigureAwait(false))
                {
                    try
                    {
                        yield return input;
                    }
                    finally
                    {
                        // The consumer asked for the next event, or stopped reading: either way it is done with this one.
                        handled.TrySetResult();
                    }
                }
            }
            finally
            {
                // Nothing reads what is left, so release every read-loop wait still parked on it.
                _ = _inputs.Writer.TryComplete();

                while (_inputs.Reader.TryRead(out (ConversationInput Input, TaskCompletionSource Handled) orphan))
                {
                    orphan.Handled.TrySetResult();
                }
            }
        }

        private Task AcceptInterruptAsync(RelayFrame.Interrupt interrupt)
        {
            if (!_sawSetup)
            {
                return Task.CompletedTask;
            }

            if (interrupt.UtteranceUntilInterrupt is null || interrupt.DurationUntilInterruptMs < 0)
            {
                if (!_loggedMalformedInterrupt)
                {
                    _loggedMalformedInterrupt = true;
                    TelnyxRelayLog.MalformedInterruptFrame(logger, conversationId());
                }

                return Task.CompletedTask;
            }

            return PublishAsync(new ConversationInput.Barge(
                interrupt.UtteranceUntilInterrupt,
                TimeSpan.FromMilliseconds(interrupt.DurationUntilInterruptMs)));
        }

        private Task PublishAsync(ConversationInput input)
        {
            TaskCompletionSource handled = new(TaskCreationOptions.RunContinuationsAsynchronously);

            return _inputs.Writer.TryWrite((input, handled)) ? handled.Task : Task.CompletedTask;
        }
    }
}

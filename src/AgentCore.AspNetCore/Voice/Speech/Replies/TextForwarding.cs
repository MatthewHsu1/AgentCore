// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/generation.py:531-569
// (_TextOutput, perform_text_forwarding, _text_forwarding_task), 679-784 (_ForwardOutput,
// forward_generation, text-only branches 729-738, 770-771, 780-781),
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#; every audio branch removed;
// each forwarding begins its own reply on the output.

using System.Text;
using AgentCore.AspNetCore.Voice.Ports;
using AgentCore.AspNetCore.Voice.Threading;

namespace AgentCore.AspNetCore.Voice.Speech.Replies
{
    /// <summary>Forwards one generation's text to <see cref="IConversationOutputPort"/>, honouring interruption.</summary>
    internal static class TextForwarding
    {
        /// <summary>
        /// Streams <paramref name="source"/> to <paramref name="output"/> until it ends or
        /// <paramref name="speechHandle"/> is interrupted, then closes the reply.
        /// </summary>
        /// <param name="speechHandle">The speech this forwarding belongs to.</param>
        /// <param name="output">Where every fragment goes.</param>
        /// <param name="source">
        /// The text, one fragment at a time. May yield an empty fragment; that fragment still counts
        /// toward <paramref name="onFirstText"/> but is never handed to <paramref name="output"/>,
        /// matching <see cref="IConversationOutputPort.SpeakAsync"/>'s "never empty" contract.
        /// </param>
        /// <param name="onFirstText">
        /// Runs once, when the first fragment (even an empty one) arrives, on the forwarding task itself, so
        /// it has finished before this method returns.
        /// </param>
        /// <param name="cancellationToken">
        /// The speech's own task token. Interruption cancels forwarding at once rather than waiting for
        /// this token's own deadline (LiveKit's <c>cancel_and_wait</c>).
        /// </param>
        /// <returns>What was forwarded, once forwarding has stopped for any reason.</returns>
        public static async Task<TextForwardingResult> ForwardAsync(
            SpeechHandle speechHandle,
            IConversationOutputPort output,
            IAsyncEnumerable<string> source,
            Action? onFirstText,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource forwardCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            StringBuilder text = new();

            // Before the task starts, so a barge-in's StopAsync also cuts off a fragment that already passed the
            // IsInterrupted check below and is still on its way to the output.
            output.BeginReply();
            Task<bool> forwardTask = ForwardTextAsync(speechHandle, output, source, text, onFirstText, forwardCancellation.Token, cancellationToken);
            await speechHandle.WaitIfNotInterruptedAsync([forwardTask]).ConfigureAwait(false);

            if (speechHandle.IsInterrupted)
            {
                await TaskTeardown.CancelAndWaitAsync(forwardCancellation, [forwardTask]).ConfigureAwait(false);
            }

            // Whole once the source ran out, even when an interruption landed after its last fragment: it cut nothing.
            bool whole = !speechHandle.IsInterrupted || (forwardTask.IsCompletedSuccessfully && forwardTask.Result);
            string forwarded = text.ToString();

            TextPlayback playback = (forwarded.Length, whole) switch
            {
                (0, _) => TextPlayback.Skipped,
                (_, true) => TextPlayback.Full,
                _ => TextPlayback.Partial,
            };
            
            return new TextForwardingResult(forwarded, playback);
        }

        /// <returns><see langword="true"/> once the source ran out; <see langword="false"/> when an interruption stopped it first.</returns>
        private static async Task<bool> ForwardTextAsync(
            SpeechHandle speechHandle,
            IConversationOutputPort output,
            IAsyncEnumerable<string> source,
            StringBuilder text,
            Action? onFirstText,
            CancellationToken forwardCancellationToken,
            CancellationToken completeCancellationToken)
        {
            bool firstTextSeen = false;
            try
            {
                await foreach (string delta in source.WithCancellation(forwardCancellationToken).ConfigureAwait(false))
                {
                    // asyncio cancels this task on the loop that ran interrupt(); here the interrupt lands on
                    // another thread, and a delta read before the cancellation arrives must not be spoken.
                    if (speechHandle.IsInterrupted)
                    {
                        return false;
                    }

                    _ = text.Append(delta);
                    if (!firstTextSeen)
                    {
                        firstTextSeen = true;

                        // Inline, not queued: the caller's "is the agent still speaking?" check after
                        // forwarding must see this, as asyncio's first_text_fut callback guarantees.
                        onFirstText?.Invoke();
                    }

                    if (delta.Length > 0)
                    {
                        await output.SpeakAsync(delta, forwardCancellationToken).ConfigureAwait(false);
                    }
                }

                return true;
            }
            finally
            {
                // Flushes even when cancelled above: LiveKit's flush() runs in the forwarding task's own
                // finally block, not in forward_generation's interruption branch.
                await output.CompleteAsync(completeCancellationToken).ConfigureAwait(false);
            }
        }
    }
}

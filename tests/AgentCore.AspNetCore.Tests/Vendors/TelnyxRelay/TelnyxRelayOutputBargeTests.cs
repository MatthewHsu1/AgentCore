using System.Buffers;
using System.Text.Json;
using AgentCore.AspNetCore.Vendors.TelnyxRelay.Connection;
using AgentCore.AspNetCore.Vendors.TelnyxRelay.Wire;
using AgentCore.AspNetCore.Voice.Ports;
using AgentCore.AspNetCore.Voice.Speech;
using AgentCore.AspNetCore.Voice.Speech.Replies;
using AgentCore.AspNetCore.Voice.Turns;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay
{
    /// <summary>What reaches the relay when a barge-in races the reply's own writes.</summary>
    public sealed class TelnyxRelayOutputBargeTests
    {
        [Fact(Timeout = 30_000)]
        public async Task ABargeBetweenTheInterruptCheckAndTheWrite_SendsNothing()
        {
            TelnyxRelayOutput output = new();
            SpeechHandle handle = NewHandle();

            // onFirstText runs after the forwarder's IsInterrupted check and before its SpeakAsync.
            _ = await TextForwarding.ForwardAsync(
                handle,
                output,
                Fragments("Sure, ", "your order shipped."),
                onFirstText: () => Barge(output, handle),
                CancellationToken.None);

            Assert.Empty(Sent(output));
        }

        [Fact(Timeout = 60_000)]
        public async Task ABargeOnAnotherThread_NeverLetsTheInterruptedReplyThroughAfterTheStop()
        {
            int leaked = 0;
            for (int run = 0; run < 2000; run++)
            {
                TelnyxRelayOutput output = new();
                SpeechHandle handle = NewHandle();
                using ManualResetEventSlim firstText = new();
                Task barge = Task.Run(
                    () =>
                    {
                        firstText.Wait();
                        Thread.SpinWait(Random.Shared.Next(0, 4000));
                        Barge(output, handle);
                    },
                    TestContext.Current.CancellationToken);

                Task<TextForwardingResult> forwarding = TextForwarding.ForwardAsync(
                    handle, output, YieldingFragments(40), onFirstText: firstText.Set, CancellationToken.None);
                await barge;
                _ = await forwarding;

                if (Sent(output).Count > 0)
                {
                    leaked++;
                }
            }

            Assert.Equal(0, leaked);
        }

        [Fact(Timeout = 30_000)]
        public async Task ABargeMidReply_SendsNoClosingTokenAfterTheStop()
        {
            TelnyxRelayOutput output = new();
            SpeechHandle handle = NewHandle();
            List<RelayToken> beforeStop = [];
            BargeBeforeWrite port = new(output, fragment =>
            {
                if (fragment == "your order shipped.")
                {
                    beforeStop.AddRange(Sent(output));
                    Barge(output, handle);
                }
            });

            _ = await TextForwarding.ForwardAsync(
                handle, port, Fragments("Sure, ", "your order shipped."), onFirstText: null, CancellationToken.None);

            Assert.Equal([new RelayToken("Sure, ", Last: false)], beforeStop);
            Assert.DoesNotContain(Sent(output), token => token.Last);
        }

        [Fact(Timeout = 30_000)]
        public async Task TheReplyAfterABarge_IsSentInFullWithItsClosingToken()
        {
            TelnyxRelayOutput output = new();
            SpeechHandle interrupted = NewHandle();
            _ = await TextForwarding.ForwardAsync(
                interrupted,
                output,
                Fragments("Sure, ", "your order shipped."),
                onFirstText: () => Barge(output, interrupted),
                CancellationToken.None);

            _ = await TextForwarding.ForwardAsync(
                NewHandle(), output, Fragments("Go ahead, ", "I'm listening."), onFirstText: null, CancellationToken.None);

            Assert.Equal(
                [
                    new RelayToken("Go ahead, ", Last: false),
                    new RelayToken("I'm listening.", Last: false),
                    new RelayToken(string.Empty, Last: true),
                ],
                Sent(output));
        }

        // A reply that never queued a fragment has nothing to close, so no closing token opens and shuts an empty reply.
        [Fact(Timeout = 30_000)]
        public async Task AReplyThatSpokeNothing_SendsNoClosingToken()
        {
            TelnyxRelayOutput output = new();

            output.BeginReply();
            await output.CompleteAsync(TestContext.Current.CancellationToken);

            Assert.Empty(Sent(output));
        }

        private static SpeechHandle NewHandle()
        {
            return SpeechHandle.Create(TimeProvider.System, NullLogger.Instance);
        }

        private static void Barge(TelnyxRelayOutput output, SpeechHandle handle)
        {
            output.StopAsync().AsTask().Wait();
            _ = handle.Interrupt(source: InterruptionSource.AudioActivity);
        }

        private static async IAsyncEnumerable<string> Fragments(params string[] fragments)
        {
            foreach (string fragment in fragments)
            {
                await Task.Yield();
                yield return fragment;
            }
        }

        private static async IAsyncEnumerable<string> YieldingFragments(int count)
        {
            for (int n = 0; n < count; n++)
            {
                await Task.Yield();
                yield return "w" + n + " ";
            }
        }

        /// <summary>Reads the queue as the write loop does, and returns each token it would send.</summary>
        private static List<RelayToken> Sent(TelnyxRelayOutput output)
        {
            List<RelayToken> sent = [];
            ArrayBufferWriter<byte> buffer = new();
            using Utf8JsonWriter writer = new(buffer);
            while (output.Reader.TryRead(out OutboundItem item))
            {
                buffer.Clear();
                writer.Reset(buffer);
                if (output.Encode(item, writer))
                {
                    sent.Add((RelayToken)item.Frame);
                }
            }

            return sent;
        }

        /// <summary>Runs a hook between the forwarder's interrupt check and the relay's own write.</summary>
        private sealed class BargeBeforeWrite(TelnyxRelayOutput inner, Action<string> beforeWrite) : IConversationOutputPort
        {
            public void BeginReply()
            {
                inner.BeginReply();
            }

            public ValueTask SpeakAsync(string fragment, CancellationToken cancellationToken = default)
            {
                beforeWrite(fragment);
                return inner.SpeakAsync(fragment, cancellationToken);
            }

            public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
            {
                return inner.CompleteAsync(cancellationToken);
            }

            public ValueTask StopAsync(CancellationToken cancellationToken = default)
            {
                return inner.StopAsync(cancellationToken);
            }

            public ValueTask DisposeAsync()
            {
                return inner.DisposeAsync();
            }
        }
    }
}

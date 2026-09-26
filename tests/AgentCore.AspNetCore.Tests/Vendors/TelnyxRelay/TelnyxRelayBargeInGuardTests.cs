using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AgentCore.Application.Runtime;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay
{
    /// <summary>
    /// What the interrupt guard does to frames already in flight: what the caller is recorded as having
    /// heard, and what must never reach the relay after the cut.
    /// </summary>
    public sealed class TelnyxRelayBargeInGuardTests
    {
        [Fact(Timeout = 30_000)]
        public async Task AnInterruptDuringAReply_RecordsWhatTheCallerHeard()
        {
            // The vendor measured both values, so nothing here is estimated. D28 and item 6a.
            //
            // BlockingChatClient's gate ignores cancellation, so a defect that left the turn
            // machinery stuck would hang this test rather than fail it red. The ten-second deadline
            // below is this test's own backstop, and reply.Release() sits in a finally so a failed
            // assertion above still opens the gate instead of leaving the server's turn blocked for
            // the rest of the host's shutdown timeout.
            using BlockingChatClient reply = new("one two three four five");
            EventObservedLoggerProvider capture = new("InterruptReceived");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                logging: logging => logging.AddProvider(capture));
            await using FakeRelayClient relay = await host.ConnectAsync();

            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token, TestContext.Current.CancellationToken);

            try
            {
                await relay.SendAsync(RelayFrames.Setup(conversationSessionId: "conversation-barge"));
                await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

                try
                {
                    _ = await relay.ReadFrameAsync().WaitAsync(bounded.Token);            // the reply started
                    await reply.WaitUntilStreamingAsync().WaitAsync(bounded.Token);

                    await relay.SendAsync(RelayFrames.Interrupt("one two", durationMs: 640));

                    // SendAsync completing only means the bytes left this client, the same way
                    // TelnyxRelayHost.WaitForSessionAsync's own remark explains for a setup frame. The
                    // connection must actually raise the turn id and conversation ConversationSession.Interrupt before
                    // the gate below opens, or a fragment already queued behind the gate could still
                    // beat the guard onto the wire.
                    await capture.Observed.WaitAsync(bounded.Token);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    Assert.Fail("the connection never reported the barge-in within ten seconds.");
                }
            }
            finally
            {
                reply.Release();
            }

            ConversationSession? session = await host.FindSessionAsync("conversation-barge");
            Assert.NotNull(session);

            TurnResult turn = await TelnyxRelayBargeInTestSupport.WaitForTurnAsync(session!);

            Assert.Equal("one two", turn.ReplyText);
            Assert.Equal(TimeSpan.FromMilliseconds(640), turn.Cut);
        }

        [Fact(Timeout = 30_000)]
        public async Task AfterAnInterrupt_NoFurtherTextFrameReachesTheRelay()
        {
            // Cancelling stops the producer. It does not stop a consumer that is mid-write, and a
            // token that lands after the interrupt makes the vendor speak audio nobody asked for.
            // pipecat names this exact straggler in its eval harness.
            //
            // This proves RunTurnAsync's own guard: interrupting before the gate on the fake model is
            // ever released means nothing has reached _outbound yet when HandleInterrupt runs, so every
            // later fragment must be stopped by the per-update check inside RunTurnAsync's own loop.
            // AFrameAlreadyInsideTheWriteLoop_IsNotFollowedByAnotherAfterAnInterrupt below covers the
            // case this test's timing cannot reach: a fragment that already reached the channel before
            // the interrupt did, sitting there while the write loop is genuinely busy elsewhere.
            using BlockingChatClient reply = new("one two three four five six seven eight");
            EventObservedLoggerProvider capture = new("InterruptReceived");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                logging: logging => logging.AddProvider(capture));
            await using FakeRelayClient relay = await host.ConnectAsync();

            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token, TestContext.Current.CancellationToken);

            try
            {
                await relay.SendAsync(RelayFrames.Setup(conversationSessionId: "conversation-race"));
                await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

                try
                {
                    _ = await relay.ReadFrameAsync().WaitAsync(bounded.Token);
                    await reply.WaitUntilStreamingAsync().WaitAsync(bounded.Token);

                    await relay.SendAsync(RelayFrames.Interrupt("one", durationMs: 300));

                    // The same reason as the test above: the gate must not open until the connection
                    // has actually raised the turn id, or this test would be timing the delivery of
                    // one WebSocket frame rather than the guard this task exists to prove.
                    await capture.Observed.WaitAsync(bounded.Token);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    Assert.Fail("the connection never reported the barge-in within ten seconds.");
                }
            }
            finally
            {
                // Release every remaining update. None of them may reach the socket. This sits in a
                // finally so a failed assertion above still lets the fake client's task finish rather
                // than leaving it, and the host's shutdown, blocked on a gate nobody will ever open.
                reply.Release();
            }

            List<JsonNode> later = await relay.ReadFramesForAsync(TimeSpan.FromMilliseconds(400));
            Assert.DoesNotContain(later, frame => frame["type"]!.GetValue<string>() == "text");
        }

        [Fact(Timeout = 30_000)]
        public async Task AnItemAlreadyQueuedBehindAStuckSend_IsRemovedByTheUnconditionalDrain()
        {
            // This does not exercise either id-based guard — not RunTurnAsync's own per-update check,
            // and not the write loop's >= comparison in front of its one SendAsync call. Both stay
            // disabled and this still passes, because HandleInterrupt's own drain — the unconditional
            // `while (_outbound.Reader.TryRead(out _)) { }` that runs before either guard is ever
            // reached — already removes the item this test cares about while it is still sitting in
            // the channel, before either comparison gets a chance to matter. What this proves is that
            // the drain reaches an item already queued, not one still in flight through a guard: a
            // single, deliberately huge fragment is the whole reply, so nothing else competes for it.
            // The write loop dequeues it almost immediately and calls SendAsync, and that call has no
            // choice but to block on genuine TCP backpressure, because nothing reads this socket until
            // this test says so. While it is stuck there, the turn's own closing token queues up right
            // behind it — still sitting in _outbound, not yet dequeued, when the interrupt fires. The
            // drain reaches it there and removes it. Only once this test finally reads does the huge
            // fragment complete and arrive, already committed to the wire before the interrupt ever
            // happened. Nothing may follow it: not the closing token the drain already removed, and
            // not anything else, even though the write loop spent this whole test genuinely busy
            // rather than idle when the interrupt landed.
            //
            // FakeRelayClient cannot prove this: its own pump reads the socket continuously in the
            // background from the moment it connects, for every other test's benefit, which means
            // nothing is ever actually stuck waiting on a reader through it — this test's first attempt
            // used it, held a fixed delay, and failed intermittently exactly because of that pump
            // draining the "stuck" frame in the background the whole time. A raw ClientWebSocket here
            // is what makes the block real: the OS send buffer this connection can autotune to (4 MiB,
            // confirmed via /proc/sys/net/ipv4/tcp_wmem on the machine this was verified on) cannot
            // absorb an 8 MiB message with no reader draining it, so SendAsync has no choice but to
            // wait. There is still no purely observable signal for "the write loop is now stuck inside
            // that wait," so the fixed delay below stands in for one, sized to outlast a same-process
            // production of one string and two channel writes many times over.
            string hugeFragment = new('x', 8 * 1024 * 1024);
            using FragmentingChatClient reply = new(hugeFragment);
            EventObservedLoggerProvider capture = new("InterruptReceived");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                logging: logging => logging.AddProvider(capture));

            using ClientWebSocket socket = new();
            await socket.ConnectAsync(host.Address, TestContext.Current.CancellationToken);

            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token, TestContext.Current.CancellationToken);

            await SendRawAsync(socket, RelayFrames.Setup(conversationSessionId: "conversation-held"));
            await SendRawAsync(socket, RelayFrames.Prompt("hi", last: true));

            await Task.Delay(300, TestContext.Current.CancellationToken);

            try
            {
                await SendRawAsync(socket, RelayFrames.Interrupt("nothing played", durationMs: 0));
                await capture.Observed.WaitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the connection never reported the barge-in within ten seconds.");
            }

            try
            {
                string stuck = await ReceiveOneMessageAsync(socket, bounded.Token);
                JsonNode node = JsonNode.Parse(stuck)!;
                Assert.Equal("text", node["type"]!.GetValue<string>());
                Assert.False(node["last"]!.GetValue<bool>());
                Assert.Equal(hugeFragment, node["token"]!.GetValue<string>());
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail(
                    "the fragment already committed to the wire before the interrupt never arrived "
                    + "within ten seconds — it may not have been stuck in the write loop at all.");
            }

            // Cancelling this receive is the proof that nothing else arrives in the window: it aborts
            // the socket, which is fine, because this is the last thing this test does with it.
            using CancellationTokenSource window = new(TimeSpan.FromMilliseconds(400));
            try
            {
                string extra = await ReceiveOneMessageAsync(socket, window.Token);
                Assert.Fail($"an unexpected frame reached the relay after the interrupt: {extra}");
            }
            catch (OperationCanceledException) when (window.IsCancellationRequested)
            {
                // Silence is the pass: nothing followed the frame already in flight.
            }
        }

        /// <summary>Sends one frame as one WebSocket message, bypassing <see cref="FakeRelayClient"/>'s own pump.</summary>
        private static Task SendRawAsync(ClientWebSocket socket, string json)
        {
            return socket.SendAsync(
                        Encoding.UTF8.GetBytes(json),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        TestContext.Current.CancellationToken);
        }

        /// <summary>Reads exactly one message off a raw socket, reassembling it from as many fragments as it takes.</summary>
        private static async Task<string> ReceiveOneMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[64 * 1024];
            using MemoryStream message = new();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("the host closed the socket before sending a frame.");
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(message.ToArray());
        }
    }
}

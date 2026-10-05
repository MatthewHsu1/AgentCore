using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Vendors.TelnyxRelay.Connection;
using AgentCore.AspNetCore.Voice.Transport;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay
{
    /// <summary>Where the relay's outbound queue meets the write loop: a stop racing a send, and a queue ended under a write.</summary>
    public sealed class TelnyxRelayOutputQueueTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // IConversationOutputPort.BeginReply: every write of a stopped reply is dropped. A frame the write loop already
        // holds when the stop lands may still go out, but only before the stop returns, never after it.
        [Fact(Timeout = 30_000)]
        public async Task AStopWhileTheWriteLoopHoldsAFrame_ReturnsOnlyOnceThatFrameIsSent()
        {
            TelnyxRelayOutput output = new();
            FakeWebSocket socket = new();
            JsonWebSocketSender sender = new(socket, TimeSpan.FromSeconds(5), CancellationToken.None);
            output.BeginReply();
            await output.SpeakAsync("Hello ", Ct);
            output.Complete();

            Thread? stopper = null;
            int sentWhenStopReturned = -1;
            await sender.WriteLoopAsync(
                output.Reader,
                (item, writer) =>
                {
                    bool encoded = output.Encode(item, writer);
                    stopper = new Thread(() =>
                    {
                        output.StopAsync(Ct).AsTask().Wait(Ct);
                        sentWhenStopReturned = socket.Sent.Count;
                    });
                    stopper.Start();

                    // The frame stays in hand until the stop has either returned or blocked: a blocked thread shows
                    // WaitSleepJoin, a returned one Stopped, and one still on its way shows neither.
                    while ((stopper.ThreadState & (ThreadState.WaitSleepJoin | ThreadState.Stopped)) == 0)
                    {
                        _ = Thread.Yield();
                    }

                    return encoded;
                },
                output.SendGate);
            stopper!.Join();

            _ = Assert.Single(socket.Sent);
            Assert.Equal(1, sentWhenStopReturned);
        }

        // A write loop that is gone never frees a writer waiting on the full queue, so ending the queue must.
        [Fact(Timeout = 30_000)]
        public async Task AWriteWaitingOnAFullQueue_ReturnsWithoutAFaultOnceTheQueueEnds()
        {
            TelnyxRelayOutput output = new();
            output.BeginReply();
            for (int word = 0; word < 256; word++)
            {
                await output.SpeakAsync("w" + word + " ", Ct);
            }

            ValueTask waiting = output.SpeakAsync("one too many", Ct);
            Assert.False(waiting.IsCompleted);

            output.Complete();

            await waiting;
            await output.CompleteAsync(Ct);
        }
    }
}

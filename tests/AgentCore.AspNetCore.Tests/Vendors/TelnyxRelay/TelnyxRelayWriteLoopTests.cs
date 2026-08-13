using System.Net.WebSockets;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay;

/// <summary>
/// What the write loop does that no real socket lets a test watch.
/// </summary>
/// <remarks>
/// Every test here drives <c>TelnyxRelayConnection.RunAsync</c> over <see cref="FakeWebSocket"/>.
/// There is no Kestrel, no port, and no client socket. Everything else about the connection is the
/// real thing, including the session factory and the store <c>AddAgentCore</c> registers.
/// </remarks>
public sealed class TelnyxRelayWriteLoopTests
{
    [Fact(Timeout = 30_000)]
    public async Task AnItemParkedBetweenTheDequeueAndTheGate_NeverReachesTheSocket()
    {
        // This is the fix that closed an earlier Critical, and it is the one place a real socket
        // cannot reach: the window between the write loop taking an item off the channel and the
        // turn-id comparison in front of its one SendAsync call. HandleInterrupt's own drain cannot
        // help here, because the item has already left the channel by the time the drain runs. Only
        // the gate can stop it, so deleting the gate must turn this test red.
        using BlockingChatClient reply = new("first second third");
        EventObservedLoggerProvider interrupted = new("InterruptReceived");
        await using var harness = RelayConnectionHarness.Start(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            logging => logging.AddProvider(interrupted));

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        // Armed before the first frame, because the write loop reads State for the first time only
        // once a turn has actually queued something.
        harness.Socket.ParkNextStateRead();
        harness.Socket.Queue(RelayFrames.Setup(callSessionId: "call-gate"));
        harness.Socket.Queue(RelayFrames.Prompt("hi", last: true));

        try
        {
            try
            {
                await harness.Socket.Parked.WaitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the write loop never reached its first item within ten seconds.");
            }

            harness.Socket.Queue(RelayFrames.Interrupt("nothing played", durationMs: 0));

            try
            {
                await interrupted.Observed.WaitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the connection never reported the barge-in within ten seconds.");
            }
        }
        finally
        {
            // Both gates are released here, so a failed assertion above still lets the write loop
            // and the model finish rather than stranding a thread for the rest of the run.
            harness.Socket.ReleaseParkedState();
            reply.Release();
        }

        harness.Socket.QueueClose();

        try
        {
            await harness.Connection.WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the connection never tore down within ten seconds.");
        }

        Assert.Empty(harness.Socket.Sent);
    }

    [Fact(Timeout = 30_000)]
    public async Task AHostThatStops_ClosesWithEndpointUnavailable()
    {
        // A real socket aborts before this status can be observed on the wire, so the assertion is
        // made on the arguments CloseOutputAsync was called with instead.
        using SequencedChatClient reply = new("hello");
        await using var harness = RelayConnectionHarness.Start(TelnyxRelayTurnTests.PolicyYaml, reply);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        harness.Socket.Queue(RelayFrames.Setup(callSessionId: "call-stopping"));
        harness.StopApplication();

        try
        {
            await harness.Connection.WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the connection never tore down after the host stopped, within ten seconds.");
        }

        var close = harness.Socket.CloseSent;
        Assert.NotNull(close);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, close!.Value.Status);
    }

    [Fact(Timeout = 30_000)]
    public async Task AWriteLoopThatFaults_ClosesWithInternalServerError()
    {
        // Nothing on a healthy loopback socket makes a send throw, so the fault is injected here.
        // The status is this endpoint's own report of its own defect, and a real call would see it.
        using SequencedChatClient reply = new("hello there caller");
        await using var harness = RelayConnectionHarness.Start(TelnyxRelayTurnTests.PolicyYaml, reply);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        harness.Socket.FailEverySend(new InvalidOperationException("the send failed."));
        harness.Socket.Queue(RelayFrames.Setup(callSessionId: "call-write-fault"));
        harness.Socket.Queue(RelayFrames.Prompt("hi", last: true));

        try
        {
            await harness.Connection.WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the connection never tore down after the write loop faulted, within ten seconds.");
        }

        var close = harness.Socket.CloseSent;
        Assert.NotNull(close);
        Assert.Equal(WebSocketCloseStatus.InternalServerError, close!.Value.Status);
    }
}

using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AgentCore.Application.Runtime;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay;

/// <summary>
/// What happens when the caller speaks over the reply.
/// </summary>
/// <remarks>
/// Every test here runs offline against a fake model and a fake relay. There is no Telnyx account,
/// no network call, and no API key anywhere in this file.
/// </remarks>
public sealed class TelnyxRelayBargeInTests
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
        await using var host = await TelnyxRelayHost.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            logging: logging => logging.AddProvider(capture));
        await using var relay = await host.ConnectAsync();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        try
        {
            await relay.SendAsync(RelayFrames.Setup(callSessionId: "call-barge"));
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

            try
            {
                await relay.ReadFrameAsync().WaitAsync(bounded.Token);            // the reply started
                await reply.WaitUntilStreamingAsync().WaitAsync(bounded.Token);

                await relay.SendAsync(RelayFrames.Interrupt("one two", durationMs: 640));

                // SendAsync completing only means the bytes left this client, the same way
                // TelnyxRelayHost.WaitForSessionAsync's own remark explains for a setup frame. The
                // connection must actually raise the turn id and call CallSession.Interrupt before
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

        var session = await host.FindSessionAsync("call-barge");
        Assert.NotNull(session);

        var turn = await WaitForTurnAsync(session!);

        Assert.Equal("one two", turn.ReplyText);
        Assert.Equal(TimeSpan.FromMilliseconds(640), turn.InterruptedAfter);
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
        await using var host = await TelnyxRelayHost.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            logging: logging => logging.AddProvider(capture));
        await using var relay = await host.ConnectAsync();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        try
        {
            await relay.SendAsync(RelayFrames.Setup(callSessionId: "call-race"));
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

            try
            {
                await relay.ReadFrameAsync().WaitAsync(bounded.Token);
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

        var later = await relay.ReadFramesForAsync(TimeSpan.FromMilliseconds(400));
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
        var hugeFragment = new string('x', 8 * 1024 * 1024);
        using SequencedChatClient reply = new(hugeFragment);
        EventObservedLoggerProvider capture = new("InterruptReceived");
        await using var host = await TelnyxRelayHost.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            logging: logging => logging.AddProvider(capture));

        using ClientWebSocket socket = new();
        await socket.ConnectAsync(host.Address, TestContext.Current.CancellationToken);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        await SendRawAsync(socket, RelayFrames.Setup(callSessionId: "call-held"));
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
            var stuck = await ReceiveOneMessageAsync(socket, bounded.Token);
            var node = JsonNode.Parse(stuck)!;
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
            var extra = await ReceiveOneMessageAsync(socket, window.Token);
            Assert.Fail($"an unexpected frame reached the relay after the interrupt: {extra}");
        }
        catch (OperationCanceledException) when (window.IsCancellationRequested)
        {
            // Silence is the pass: nothing followed the frame already in flight.
        }
    }

    /// <summary>Sends one frame as one WebSocket message, bypassing <see cref="FakeRelayClient"/>'s own pump.</summary>
    private static Task SendRawAsync(ClientWebSocket socket, string json)
        => socket.SendAsync(
            Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text,
            endOfMessage: true,
            TestContext.Current.CancellationToken);

    /// <summary>Reads exactly one message off a raw socket, reassembling it from as many fragments as it takes.</summary>
    private static async Task<string> ReceiveOneMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
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

    [Fact(Timeout = 30_000)]
    public async Task AnInterruptWithNoTurnRunning_ChangesNothing()
    {
        using SequencedChatClient reply = new("hello");
        await using var host = await TelnyxRelayHost.StartAsync(TelnyxRelayTurnTests.PolicyYaml, reply);
        await using var relay = await host.ConnectAsync();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        await relay.SendAsync(RelayFrames.Setup());
        await relay.SendAsync(RelayFrames.Interrupt("nothing played", durationMs: 10));
        await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

        try
        {
            Assert.Equal("hello", string.Concat(await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token)));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the turn after the ignored interrupt never finished within ten seconds.");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task AnInterruptWithNoUtteranceUntilInterrupt_IsRefusedAndTheCallContinues()
    {
        // Section 7.1's rule for an unknown frame type applies here too: a frame the vendor got
        // wrong must not drop the call. RelayFrames.Interrupt always emits both fields, so a raw
        // frame is sent by hand here, missing utteranceUntilInterrupt entirely — the one shape
        // HandleInterrupt's own guard exists to survive, since System.Text.Json deserializes a
        // missing non-nullable string as null rather than throwing, and CallSession.Interrupt
        // itself would otherwise take an ArgumentNullException straight into the read loop.
        using SequencedChatClient reply = new("hello");
        EventObservedLoggerProvider capture = new("MalformedInterruptFrame");
        await using var host = await TelnyxRelayHost.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            logging: logging => logging.AddProvider(capture));
        await using var relay = await host.ConnectAsync();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        await relay.SendAsync(RelayFrames.Setup());
        await relay.SendRawAsync("""{"type":"interrupt","durationUntilInterruptMs":10}""");

        try
        {
            await capture.Observed.WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the connection never reported refusing the malformed interrupt within ten seconds.");
        }

        await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

        try
        {
            Assert.Equal("hello", string.Concat(await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token)));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the turn after the refused interrupt never finished within ten seconds.");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task AnInterruptWithANegativeDuration_IsRefusedAndTheCallContinues()
    {
        // The same guard, the other malformed shape it exists for: System.Text.Json enforces no
        // range check on durationUntilInterruptMs, so a negative value would otherwise reach
        // CallSession.Interrupt's own ArgumentOutOfRangeException uncaught and take the read loop
        // down with it. D28 forbids clamping it into something plausible instead of refusing it.
        using SequencedChatClient reply = new("hello");
        EventObservedLoggerProvider capture = new("MalformedInterruptFrame");
        await using var host = await TelnyxRelayHost.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            logging: logging => logging.AddProvider(capture));
        await using var relay = await host.ConnectAsync();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        await relay.SendAsync(RelayFrames.Setup());
        await relay.SendRawAsync(
            """{"type":"interrupt","utteranceUntilInterrupt":"hello","durationUntilInterruptMs":-5}""");

        try
        {
            await capture.Observed.WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the connection never reported refusing the malformed interrupt within ten seconds.");
        }

        await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

        try
        {
            Assert.Equal("hello", string.Concat(await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token)));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the turn after the refused interrupt never finished within ten seconds.");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ASecondFinalPromptDuringATurn_RunsAfterTheFirstTurnEnds()
    {
        // The caller can finish a second sentence before the agent speaks. There is no interrupt
        // frame, so there is no heard text, and IConversationPort throws on a second turn. Holding
        // one prompt keeps the caller's words; throwing would drop the call.
        //
        // reply.Calls == 2 alone cannot tell the held path from an accident: if the release below
        // happened to win the race against the server actually dispatching "two" while the first
        // turn was still running, "two" would just start its own ordinary second turn instead of
        // being held — and Calls would still read 2, the same as it would against code with no
        // pending-prompt feature at all. Waiting for PromptHeld closes that gap: it only fires from
        // inside the branch that holds a prompt, so seeing it is proof this is the path that ran.
        //
        // BlockingChatClient's gate ignores cancellation, so a defect in the pending-prompt chain
        // would hang this test rather than fail it red, hence the deadline below.
        using BlockingChatClient reply = new("first reply");
        EventObservedLoggerProvider capture = new("PromptHeld");
        await using var host = await TelnyxRelayHost.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            logging: logging => logging.AddProvider(capture));
        await using var relay = await host.ConnectAsync();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        try
        {
            await relay.SendAsync(RelayFrames.Setup());
            await relay.SendAsync(RelayFrames.Prompt("one", last: true));

            try
            {
                await reply.WaitUntilStreamingAsync().WaitAsync(bounded.Token);

                await relay.SendAsync(RelayFrames.Prompt("two", last: true));
                await capture.Observed.WaitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the connection never reported holding the second prompt within ten seconds.");
            }
        }
        finally
        {
            reply.Release();
        }

        try
        {
            await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token);   // the first turn
            await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token);   // the held one
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the held prompt never ran its turn within ten seconds.");
        }

        Assert.Equal(2, reply.Calls);
    }

    [Fact(Timeout = 30_000)]
    public async Task AThirdFinalPromptDuringATurn_IsDroppedAndLoggedOnce()
    {
        // Item 6a of section 11 draws the line at one held prompt. This proves the line actually
        // holds: a third and a fourth final prompt, arriving while one is already held, both run no
        // turn of their own, and PendingPromptDropped — which the connection only ever logs once
        // for the call — fires exactly once despite two drops, not once per dropped frame.
        using BlockingChatClient reply = new("first reply");
        EventObservedLoggerProvider heldCapture = new("PromptHeld");
        EventObservedLoggerProvider droppedCapture = new("PendingPromptDropped");
        await using var host = await TelnyxRelayHost.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            logging: logging =>
            {
                logging.AddProvider(heldCapture);
                logging.AddProvider(droppedCapture);
            });
        await using var relay = await host.ConnectAsync();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        try
        {
            await relay.SendAsync(RelayFrames.Setup());
            await relay.SendAsync(RelayFrames.Prompt("one", last: true));

            try
            {
                await reply.WaitUntilStreamingAsync().WaitAsync(bounded.Token);

                await relay.SendAsync(RelayFrames.Prompt("two", last: true));
                await heldCapture.Observed.WaitAsync(bounded.Token);

                await relay.SendAsync(RelayFrames.Prompt("three", last: true));
                await droppedCapture.Observed.WaitAsync(bounded.Token);

                // A fourth cannot raise a second log line to wait on — the flag that guards it
                // never resets. Releasing the gate right after SendAsync returns would only prove
                // the bytes left this client, the same gap every other wait in this file closes:
                // reply.Release() below unblocks turn one, which can cascade through
                // RunPendingPrompt and clear _pendingPrompt for the held "two" before "four" has
                // even reached the read loop — and a "four" that arrives to find nothing pending
                // is held for a turn of its own instead of dropped, rather than proving anything
                // about a second drop. This waits for the read loop to have plainly had enough
                // time to reach and drop a small already-received frame, which needs nothing past
                // dispatch and a lock acquisition, before the gate opens.
                await relay.SendAsync(RelayFrames.Prompt("four", last: true));
                await Task.Delay(200, TestContext.Current.CancellationToken);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the connection never reported holding or dropping a prompt within ten seconds.");
            }
        }
        finally
        {
            reply.Release();
        }

        try
        {
            await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token);   // "one"
            await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token);   // the held "two"
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the held prompt never ran its turn within ten seconds.");
        }

        Assert.Equal(2, reply.Calls);
        Assert.Equal(1, droppedCapture.Count);
    }

    [Fact(Timeout = 30_000)]
    public async Task ATurnAfterABargeIn_AnswersNormally()
    {
        // Item 6a is half proved by AfterAnInterrupt_NoFurtherTextFrameReachesTheRelay: a barge-in
        // ends one turn cleanly. The other half — the call itself goes on — needs its own proof,
        // because nothing else here shows the caller can speak again and get a real answer rather
        // than silence or a leftover interrupted reply.
        using BlockingChatClient reply = new("hello there caller");
        EventObservedLoggerProvider capture = new("InterruptReceived");
        await using var host = await TelnyxRelayHost.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            logging: logging => logging.AddProvider(capture));
        await using var relay = await host.ConnectAsync();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        try
        {
            await relay.SendAsync(RelayFrames.Setup(callSessionId: "call-continues"));
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

            try
            {
                await relay.ReadFrameAsync().WaitAsync(bounded.Token);
                await reply.WaitUntilStreamingAsync().WaitAsync(bounded.Token);

                await relay.SendAsync(RelayFrames.Interrupt("hello", durationMs: 200));
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

        var session = await host.FindSessionAsync("call-continues");
        Assert.NotNull(session);

        var firstTurn = await WaitForTurnAsync(session!);
        Assert.Equal(TimeSpan.FromMilliseconds(200), firstTurn.InterruptedAfter);

        await relay.SendAsync(RelayFrames.Prompt("go on", last: true));

        try
        {
            var tokens = await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token);
            Assert.Equal("hello there caller", string.Concat(tokens));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the turn after the barge-in never finished within ten seconds.");
        }

        Assert.Null(session!.LastTurn!.InterruptedAfter);
    }

    [Fact(Timeout = 30_000)]
    public async Task AnInterruptWhileAHeldTurnHasSaidNothing_LeavesThatTurnFreeToSpeak()
    {
        // Telnyx paces the audio, so turn one is still playing when RunPendingPrompt starts turn
        // two inside turn one's own finally. A barge-in that marks the turn running *now* would
        // silence turn two, drop its closing frame, and leave the caller listening to dead air.
        // The interrupt belongs to the turn whose output the caller was actually hearing.
        using HeldPromptChatClient reply = new("first reply", "second reply");
        EventObservedLoggerProvider held = new("PromptHeld");
        EventObservedLoggerProvider interrupted = new("InterruptReceived");
        await using var host = await TelnyxRelayHost.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            logging: logging =>
            {
                logging.AddProvider(held);
                logging.AddProvider(interrupted);
            });
        await using var relay = await host.ConnectAsync();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        try
        {
            await relay.SendAsync(RelayFrames.Setup(callSessionId: "call-held-turn"));
            await relay.SendAsync(RelayFrames.Prompt("one", last: true));

            try
            {
                await reply.WaitUntilFirstTurnStreamingAsync().WaitAsync(bounded.Token);

                // The second sentence lands while turn one still runs, so it is held rather than
                // started, which is the only way to reach the two-turn overlap this test needs.
                await relay.SendAsync(RelayFrames.Prompt("two", last: true));
                await held.Observed.WaitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the connection never reported holding the second prompt within ten seconds.");
            }
        }
        finally
        {
            reply.ReleaseFirstTurn();
        }

        try
        {
            // Turn one's whole reply, closing frame included, is on the wire before the barge-in.
            var first = await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token);
            Assert.Equal("first reply", string.Concat(first));

            // Turn two is now running and has produced nothing. The caller is still hearing turn one.
            await reply.SecondTurnStarted.Task.WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the held turn never started within ten seconds.");
        }

        try
        {
            await relay.SendAsync(RelayFrames.Interrupt("first", durationMs: 640));
            await interrupted.Observed.WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the connection never reported the barge-in within ten seconds.");
        }
        finally
        {
            reply.ReleaseSecondTurn();
        }

        try
        {
            var second = await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token);
            Assert.Equal("second reply", string.Concat(second));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail(
                "the held turn's answer never reached the relay within ten seconds; the barge-in "
                + "silenced a turn the caller had not heard.");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task AnInterruptWhileAHeldTurnHasOnlyThoughtAloud_StillAmendsTheTurnTheCallerHeard()
    {
        // The sibling test above holds turn two before it produces anything at all, and both sides
        // of the seam then agree on their own: the connection marks _spokenTurnId, which still names
        // turn one, and CallSession finds its own run not audible. This test closes the gap between
        // those two answers. A run becomes audible to CallSession at its first piece of *content*,
        // and content is not a word: a tool call, a tool result, or a line of reasoning makes turn
        // two audible to the core while not one syllable of it has reached the relay. The connection
        // still marks turn one, correctly, and still lets turn two speak — but CallSession, asked
        // nothing about which turn was heard, would cut turn two at its first thought and write the
        // caller's heard text onto a turn nobody heard. cutsRunningTurn is the answer the connection
        // already has, carried across the seam so the core stops guessing.
        using HeldPromptChatClient reply = new("first reply", "second reply")
        {
            SecondTurnOpensWithUnspokenContent = true,
        };
        EventObservedLoggerProvider held = new("PromptHeld");
        EventObservedLoggerProvider interrupted = new("InterruptReceived");
        await using var host = await TelnyxRelayHost.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            logging: logging =>
            {
                logging.AddProvider(held);
                logging.AddProvider(interrupted);
            });
        await using var relay = await host.ConnectAsync();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        try
        {
            await relay.SendAsync(RelayFrames.Setup(callSessionId: "call-thinking-turn"));
            await relay.SendAsync(RelayFrames.Prompt("one", last: true));

            try
            {
                await reply.WaitUntilFirstTurnStreamingAsync().WaitAsync(bounded.Token);

                await relay.SendAsync(RelayFrames.Prompt("two", last: true));
                await held.Observed.WaitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the connection never reported holding the second prompt within ten seconds.");
            }
        }
        finally
        {
            reply.ReleaseFirstTurn();
        }

        var session = await host.FindSessionAsync("call-thinking-turn");
        Assert.NotNull(session);

        try
        {
            // Turn one's whole reply, closing frame included, is on the wire before the barge-in.
            Assert.Equal("first reply", string.Concat(await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token)));

            // Turn two is now running and has thought aloud once. The core calls it audible; the
            // caller has heard nothing of it, because reasoning carries no text frame.
            await reply.SecondTurnStarted.Task.WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the held turn never reached its first thought within ten seconds.");
        }

        var first = session!.LastTurn;
        Assert.NotNull(first);
        Assert.Null(first!.InterruptedAfter);

        try
        {
            await relay.SendAsync(RelayFrames.Interrupt("first", durationMs: 640));
            await interrupted.Observed.WaitAsync(bounded.Token);

            // Read before the gate opens, while turn one is still the turn that finished last. D28:
            // both values are the ones the vendor reported, and nothing here estimated either.
            Assert.Equal(first.TurnIndex, session.LastTurn!.TurnIndex);
            Assert.Equal("first", session.LastTurn.ReplyText);
            Assert.Equal(TimeSpan.FromMilliseconds(640), session.LastTurn.InterruptedAfter);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the connection never reported the barge-in within ten seconds.");
        }
        finally
        {
            reply.ReleaseSecondTurn();
        }

        try
        {
            var second = await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token);
            Assert.Equal("second reply", string.Concat(second));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail(
                "the held turn's answer never reached the relay within ten seconds; the barge-in "
                + "cut a turn the caller had only heard silence from.");
        }

        // Turn two ran to its own end. Nothing wrote the heard text onto it.
        Assert.Equal("second reply", session.LastTurn!.ReplyText);
        Assert.Null(session.LastTurn.InterruptedAfter);
    }

    /// <summary>Polls for the turn a barge-in ends, bounded the same way <c>TelnyxRelayHost</c>'s own waits are.</summary>
    private static async Task<TurnResult> WaitForTurnAsync(CallSession session)
    {
        for (var attempt = 0; attempt < 1_000; attempt++)
        {
            if (session.LastTurn is { } turn)
            {
                return turn;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("the interrupted turn never finished within ten seconds.");
    }
}

/// <summary>
/// Completes once the connection writes the log line named <c>eventName</c>, and counts how many
/// times it does.
/// </summary>
/// <remarks>
/// A relay client's <c>SendAsync</c> completing only proves the bytes left the client, not that the
/// connection has read, dispatched, and finished handling them — the same gap
/// <see cref="TelnyxRelayHost.WaitForSessionAsync"/> documents for a setup frame. A test that
/// released a gate on <c>SendAsync</c> alone would be timing delivery, not the connection's own
/// state, so several tests in this file wait for a named line first. Matched by
/// <see cref="EventId.Name"/>, the same way <see cref="DtmfObservedLoggerProvider"/> already is,
/// because none of the words a caller said ever reach a log line to match on instead.
/// </remarks>
internal sealed class EventObservedLoggerProvider(string eventName) : ILoggerProvider
{
    private readonly TaskCompletionSource _observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;
    private LogLevel? _level;

    /// <summary>Gets a task that completes once the connection first logs the named line.</summary>
    public Task Observed => _observed.Task;

    /// <summary>Gets how many times the connection has logged the named line so far.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Gets the level of the most recent match, or null before the first one.</summary>
    /// <remarks>
    /// Matching on <see cref="EventId.Name"/> alone proves a line fired; a test that also cares
    /// which severity it fired at — a protocol violation logging at Warning rather than the Error
    /// level an unhandled defect gets — reads this alongside <see cref="Observed"/>.
    /// </remarks>
    public LogLevel? Level => _level;

    public ILogger CreateLogger(string categoryName) => new Logger(this, eventName);

    public void Dispose()
    {
        // Nothing to release.
    }

    private sealed class Logger(EventObservedLoggerProvider owner, string eventName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == eventName)
            {
                owner._level = logLevel;
                Interlocked.Increment(ref owner._count);
                owner._observed.TrySetResult();
            }
        }
    }
}

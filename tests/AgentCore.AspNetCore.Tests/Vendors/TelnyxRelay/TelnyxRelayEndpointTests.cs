using System.Net;
using System.Net.WebSockets;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Vendors.TelnyxRelay;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay;

/// <summary>
/// How the route behaves before a call, and how the socket ends.
/// </summary>
/// <remarks>
/// Every test here runs offline against a fake model and a fake relay. There is no Telnyx account,
/// no network call, and no API key anywhere in this file.
/// </remarks>
public sealed class TelnyxRelayEndpointTests
{
    [Fact(Timeout = 30_000)]
    public async Task APlainGet_IsRefused()
    {
        using SequencedChatClient reply = new("hello");
        await using var host = await TelnyxRelayHost.StartAsync(TelnyxRelayTurnTests.PolicyYaml, reply);

        var answer = await host.GetAsync(TelnyxRelayEndpointRouteBuilderExtensions.DefaultPattern);

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
    }

    [Fact(Timeout = 30_000)]
    public async Task AHostThatForgotUseWebSockets_FailsWithAMessageThatNamesTheFix()
    {
        // The library maps an endpoint, and UseWebSockets is middleware the host owns. Without it
        // the endpoint throws InvalidOperationException rather than answering 400 — ASP.NET Core's
        // own hosting layer has no exception-handling middleware here, so ASP.NET Core turns that
        // unhandled throw into an empty 500, and the reason must not be a mystery in the log.
        using SequencedChatClient reply = new("hello");
        await using var host = await TelnyxRelayHost.StartWithoutWebSocketsAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply);

        var answer = await host.GetAsync(TelnyxRelayEndpointRouteBuilderExtensions.DefaultPattern);

        Assert.Equal(HttpStatusCode.InternalServerError, answer.StatusCode);
        Assert.Contains("UseWebSockets", host.LastError, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // The three option-range facts that stood here are now in
    // AgentCore.AspNetCore.Tests/Call/CallOptionsFromDocumentTests. The limits come out of
    // providers.call rather than out of a C# argument, so the same three ranges are refused by a
    // ConfigurationLoadException carrying the pointer of the offending field, and there is no
    // longer a map-time ArgumentOutOfRangeException for this file to assert. Spec §12.
    // ---------------------------------------------------------------------------------------------

    [Fact(Timeout = 30_000)]
    public async Task MalformedJson_ClosesWithInvalidPayloadData()
    {
        using SequencedChatClient reply = new("hello");
        await using var host = await TelnyxRelayHost.StartAsync(TelnyxRelayTurnTests.PolicyYaml, reply);
        await using var relay = await host.ConnectAsync();

        await relay.SendRawAsync("{ this is not JSON");

        var close = await relay.ReadCloseAsync();
        Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, close.Status);
    }

    [Fact(Timeout = 30_000)]
    public async Task MalformedJson_LogsTheRefusalAtWarningNotError()
    {
        // Round 1, item 2: a protocol violation is the relay's own mistake, not this endpoint's
        // bug, and DetermineCloseStatus already carries the right close status off the same
        // exception. RelayProtocolViolation must log it at Warning — the level PromptBeforeSetup,
        // FrameRefused, and MalformedInterruptFrame already use for a bad frame — and not at the
        // Error level ReadLoopFaulted uses for an unhandled defect.
        using SequencedChatClient reply = new("hello");
        EventObservedLoggerProvider capture = new("RelayProtocolViolation");
        await using var host = await TelnyxRelayHost.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            logging: logging => logging.AddProvider(capture));
        await using var relay = await host.ConnectAsync();

        await relay.SendRawAsync("{ this is not JSON");

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        try
        {
            await capture.Observed.WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the connection never logged the protocol violation within ten seconds.");
        }

        Assert.Equal(LogLevel.Warning, capture.Level);
    }

    [Fact(Timeout = 30_000)]
    public async Task AFrameOverTheLimit_ClosesWithMessageTooBig()
    {
        using SequencedChatClient reply = new("hello");
        await using var host = await TelnyxRelayHost.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            relay: options => options.MaxFrameBytes = 256);
        await using var relay = await host.ConnectAsync();

        await relay.SendAsync(RelayFrames.Prompt(new string('a', 1024), last: true));

        var close = await relay.ReadCloseAsync();
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, close.Status);
    }

    [Fact(Timeout = 30_000)]
    public async Task AClientThatVanishesWithNoCloseFrame_ReleasesTheSession()
    {
        // The vendor never reconnects, so a dead socket is a finished call.
        using SequencedChatClient reply = new("hello");
        await using var host = await TelnyxRelayHost.StartAsync(TelnyxRelayTurnTests.PolicyYaml, reply);

        var relay = await host.ConnectAsync();
        await relay.SendAsync(RelayFrames.Setup(callSessionId: "call-dropped"));
        await host.WaitForSessionAsync("call-dropped");

        relay.Abort();

        await host.WaitForCallEndAsync("call-dropped");
    }

    [Fact(Timeout = 30_000)]
    public async Task ASocketThatGoesSilent_EndsTheCallAtTheIdleDeadline()
    {
        // The vendor never reconnects, so a silent socket is a call that already ended. Nothing
        // else notices it: the keep-alive ping only catches a peer the network itself stopped
        // answering. The clock is a FakeTimeProvider, moved forward below with no real sleep at
        // all, so this test proves the idle deadline fires rather than passing because the
        // timeout never triggers — if it never fired, closing would still be incomplete when the
        // bounded wait below gives up.
        FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
        EventObservedLoggerProvider capture = new("IdleTimeoutReached");
        using SequencedChatClient reply = new("hello");
        await using var host = await TelnyxRelayHost.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            configure: options => options.TimeProvider = clock,
            logging: logging => logging.AddProvider(capture),
            relay: options => options.IdleTimeout = TimeSpan.FromSeconds(30));
        await using var relay = await host.ConnectAsync();

        await relay.SendAsync(RelayFrames.Setup(callSessionId: "call-silent"));
        await host.WaitForSessionAsync("call-silent");

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        var closing = relay.ReadCloseAsync();
        (WebSocketCloseStatus? Status, string? Description) close;
        try
        {
            // WaitForSessionAsync only proves the setup frame reached the store; it says nothing
            // about whether the read loop has already armed its next idle deadline against this
            // clock by the time this runs. Advancing once could land before that deadline exists
            // and move nothing that matters. Advancing repeatedly instead closes that race:
            // whichever call lands after the deadline is armed pushes the clock straight past its
            // due time, however many earlier calls landed too early to move anything.
            while (!closing.IsCompleted)
            {
                clock.Advance(TimeSpan.FromSeconds(31));
                await Task.Delay(20, bounded.Token);
            }

            close = await closing;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the socket never closed within ten seconds of the idle deadline firing.");
            return;
        }

        Assert.Equal(WebSocketCloseStatus.NormalClosure, close.Status);
        Assert.True(capture.Observed.IsCompletedSuccessfully, "the idle deadline closed the socket without logging IdleTimeoutReached.");
        Assert.Equal(LogLevel.Information, capture.Level);

        await host.WaitForCallEndAsync("call-silent");
    }

    [Fact(Timeout = 30_000)]
    public async Task ABusyCall_NeverTimesOutBetweenMessages()
    {
        // Task 8: each inbound frame resets the idle clock. Proven here by advancing the fake
        // clock five times, each step under IdleTimeout on its own but well over it summed — a
        // clock that failed to reset on every dtmf frame between the steps would have closed this
        // call by the second step at the latest. Dtmf is the frame to drive this with because
        // TelnyxRelayLog.DtmfReceived logs no argument at all, so counting it never risks logging
        // the digit itself.
        const int steps = 5;
        FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
        EventObservedLoggerProvider dtmf = new("DtmfReceived");
        using SequencedChatClient reply = new("hello");
        await using var host = await TelnyxRelayHost.StartAsync(
            TelnyxRelayTurnTests.PolicyYaml,
            reply,
            configure: options => options.TimeProvider = clock,
            logging: logging => logging.AddProvider(dtmf),
            relay: options => options.IdleTimeout = TimeSpan.FromSeconds(2));
        await using var relay = await host.ConnectAsync();

        await relay.SendAsync(RelayFrames.Setup(callSessionId: "call-busy"));
        await host.WaitForSessionAsync("call-busy");

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        try
        {
            for (var step = 0; step < steps; step++)
            {
                // 1.5s against a 2s deadline: under the deadline on its own, five times over it
                // summed, so this can only pass if every dtmf frame below actually rearms the
                // deadline rather than leaving the one armed at setup still counting down.
                clock.Advance(TimeSpan.FromSeconds(1.5));
                await relay.SendAsync(RelayFrames.Dtmf("1"));

                // Waits for this step's own dtmf frame to be logged, rather than a fixed real
                // sleep: a guessed delay could still be running when the next iteration's Advance
                // fires, landing a second advance against the deadline armed for this message
                // before the server had a chance to rearm it for the next one — a false close that
                // would have nothing to do with whether resets actually work. Watching the count
                // reach this step, the same way the interrupt tests wait on a log line rather than
                // sleeping, is what keeps every Advance call behind the message it depends on.
                var expected = step + 1;
                while (dtmf.Count < expected)
                {
                    await Task.Delay(5, bounded.Token);
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail($"only {dtmf.Count} of {steps} dtmf frames were processed within ten seconds; the call likely closed early.");
            return;
        }

        Assert.NotNull(await host.FindSessionAsync("call-busy"));
    }
}

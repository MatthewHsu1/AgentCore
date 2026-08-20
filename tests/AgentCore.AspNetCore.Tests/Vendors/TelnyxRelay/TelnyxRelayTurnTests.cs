using AgentCore.TestSupport;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay;

/// <summary>
/// One relay socket runs one call, and one final prompt runs one turn.
/// </summary>
/// <remarks>
/// Every test here runs offline against a fake model and a fake relay. There is no Telnyx account,
/// no network call, and no API key anywhere in this file.
/// </remarks>
public sealed class TelnyxRelayTurnTests
{
    internal const string PolicyYaml =
        """
        apiVersion: agentcore/v1
        name: relay-turn-loop
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: greeter, instructions: "greet the caller" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    // -------------------------------------------------------------------------------------------
    // The happy path.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFinalPrompt_RunsOneTurnAndSendsOneTextFrameForEachUpdate()
    {
        using FragmentingChatClient reply = new("hello there caller");
        await using var host = await TelnyxRelayHost.StartAsync(PolicyYaml, reply);
        await using var relay = await host.ConnectAsync();

        await relay.SendAsync(RelayFrames.Setup());
        await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

        var tokens = await relay.ReadTextFramesUntilLastAsync();

        Assert.True(tokens.Count > 1, "the reply must stream, not arrive as one frame.");
        Assert.Equal("hello there caller", string.Concat(tokens));
    }

    [Fact]
    public async Task ASetupFrame_NamesTheCallAfterTheCallSessionId()
    {
        // callSessionId groups the legs of one logical call, so it survives the warm transfer of
        // slice 2. A leg id would not.
        using FragmentingChatClient reply = new("hello");
        await using var host = await TelnyxRelayHost.StartAsync(PolicyYaml, reply);
        await using var relay = await host.ConnectAsync();

        await relay.SendAsync(RelayFrames.Setup(callSessionId: "logical-call-7"));
        await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
        await relay.ReadTextFramesUntilLastAsync();

        var session = await host.FindSessionAsync("logical-call-7");
        Assert.NotNull(session);
        Assert.Equal("logical-call-7", session.CallId);
    }

    // -------------------------------------------------------------------------------------------
    // The rule this task exists for: the read loop never waits for the turn.
    // -------------------------------------------------------------------------------------------

    [Fact(Timeout = 30_000)]
    public async Task AFurtherFrame_ReachesTheConnectionWhileTheReplyIsStillStreaming()
    {
        // A read loop that awaited RunTurnStreamingAsync directly, rather than starting it and
        // moving on, could not call ReceiveAsync again until the reply finished. The dtmf frame
        // below is sent, and observed to have landed, before the gate on the reply is ever
        // released — so a read loop that blocked on the turn would leave the "await capture" line
        // hanging rather than let this test finish. Dtmf carries no barge-in logic of its own, so
        // this proves only the rule this task owns, and nothing Task 6 will add.
        //
        // No test in this project carries a timeout, so TestContext.Current.CancellationToken never
        // fires on its own for one test. The ten-second deadline below is this test's own backstop,
        // and the [Fact(Timeout)] above is the second one in case that backstop itself never runs.
        // reply.Release() lives in a finally so a failed assertion above still opens the gate,
        // rather than leaving the server's turn blocked for the rest of the host's shutdown timeout.
        using BlockingChatClient reply = new("hello there caller");
        DtmfObservedLoggerProvider capture = new();
        await using var host = await TelnyxRelayHost.StartAsync(
            PolicyYaml,
            reply,
            logging: logging => logging.AddProvider(capture));
        await using var relay = await host.ConnectAsync();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        try
        {
            await relay.SendAsync(RelayFrames.Setup());
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

            // The gate opens right after the first fragment leaves, so the turn is still streaming
            // here.
            try
            {
                await reply.WaitUntilStreamingAsync().WaitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the reply never started streaming within ten seconds.");
            }

            await relay.SendAsync(RelayFrames.Dtmf("5"));

            try
            {
                await capture.Observed.WaitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail(
                    "the connection never logged that a digit arrived within ten seconds; the read "
                    + "loop may be blocked on the turn.");
            }
        }
        finally
        {
            reply.Release();
        }

        var tokens = await relay.ReadTextFramesUntilLastAsync();
        Assert.Equal("hello there caller", string.Concat(tokens));
    }

    // -------------------------------------------------------------------------------------------
    // What the read loop must survive.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnInterimPrompt_RunsNoTurn()
    {
        // The relay sends many interim transcripts per turn. One turn per partial word would run
        // the model many times over and speak nonsense.
        //
        // Calls == 1 alone would not catch a reader that started a turn on every prompt frame: the
        // later interim prompts would then hit the in-flight guard and be dropped for the wrong
        // reason, and Calls would still read 1. Asserting on the text the model actually saw is
        // what tells the two apart — the model must see the final transcript, never an interim one.
        using FragmentingChatClient reply = new("hello");
        await using var host = await TelnyxRelayHost.StartAsync(PolicyYaml, reply);
        await using var relay = await host.ConnectAsync();

        await relay.SendAsync(RelayFrames.Setup());
        await relay.SendAsync(RelayFrames.Prompt("hel", last: false));
        await relay.SendAsync(RelayFrames.Prompt("hello th", last: false));
        await relay.SendAsync(RelayFrames.Prompt("hello there", last: true));

        await relay.ReadTextFramesUntilLastAsync();

        Assert.Equal(1, reply.Calls);
        Assert.NotNull(reply.LastRequest);
        Assert.Contains(reply.LastRequest!, message => message.Text == "hello there");
    }

    [Fact]
    public async Task APromptSplitAcrossThreeFragments_ArrivesAsOneFrame()
    {
        using FragmentingChatClient reply = new("hello");
        await using var host = await TelnyxRelayHost.StartAsync(PolicyYaml, reply);
        await using var relay = await host.ConnectAsync();

        await relay.SendAsync(RelayFrames.Setup());
        await relay.SendFragmentedAsync(RelayFrames.Prompt("hi", last: true), fragments: 3);

        var tokens = await relay.ReadTextFramesUntilLastAsync();
        Assert.Equal("hello", string.Concat(tokens));
    }

    [Fact]
    public async Task AnUnknownFrameType_IsIgnoredAndTheCallGoesOn()
    {
        using FragmentingChatClient reply = new("hello");
        await using var host = await TelnyxRelayHost.StartAsync(PolicyYaml, reply);
        await using var relay = await host.ConnectAsync();

        await relay.SendAsync(RelayFrames.Setup());
        await relay.SendRawAsync("""{"type":"whisper","text":"a frame from next year"}""");
        await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

        Assert.Equal("hello", string.Concat(await relay.ReadTextFramesUntilLastAsync()));
    }

    [Fact(Timeout = 30_000)]
    public async Task ADecimalDurationOnAnInterruptFrame_IsRefusedAndTheCallGoesOn()
    {
        // Section 7.1: a vendor that changes a frame must not be able to drop a call. The type is
        // known and only one field will not bind, so the frame is refused and the socket lives.
        // A decimal here would otherwise end a live call at the exact moment of a barge-in.
        using FragmentingChatClient reply = new("hello");
        EventObservedLoggerProvider capture = new("FrameBodyRefused");
        await using var host = await TelnyxRelayHost.StartAsync(
            PolicyYaml,
            reply,
            logging: logging => logging.AddProvider(capture));
        await using var relay = await host.ConnectAsync();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        await relay.SendAsync(RelayFrames.Setup());
        await relay.SendRawAsync(
            """{"type":"interrupt","utteranceUntilInterrupt":"hello","durationUntilInterruptMs":1820.5}""");

        try
        {
            await capture.Observed.WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the connection never reported refusing the frame body within ten seconds.");
        }

        Assert.Equal(LogLevel.Warning, capture.Level);

        await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

        try
        {
            Assert.Equal("hello", string.Concat(await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token)));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the turn after the refused frame never finished within ten seconds.");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ANonStringCustomParameterOnASetupFrame_IsRefusedAndTheCallGoesOn()
    {
        // The same rule, on the one frame that starts a call. customParameters binds to a
        // dictionary of strings, so a number in it will not bind, and refusing the whole socket
        // would kill the call before it ever began.
        using FragmentingChatClient reply = new("hello");
        EventObservedLoggerProvider capture = new("FrameBodyRefused");
        await using var host = await TelnyxRelayHost.StartAsync(
            PolicyYaml,
            reply,
            logging: logging => logging.AddProvider(capture));
        await using var relay = await host.ConnectAsync();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        await relay.SendRawAsync(
            """
            {"type":"setup","sessionId":"session-one","callSid":"v2:leg-one",
             "callControlId":"v2:leg-one","callSessionId":"call-bad-parameters","callLegId":"leg-one",
             "from":"+13122010094","to":"+13122123456","direction":"inbound",
             "customParameters":{"a":7},"callStatus":"active"}
            """);

        try
        {
            await capture.Observed.WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the connection never reported refusing the setup body within ten seconds.");
        }

        // The socket is still usable, which is the whole point: the vendor gets another chance.
        await relay.SendAsync(RelayFrames.Setup(callSessionId: "call-after-bad-setup"));
        await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

        try
        {
            Assert.Equal("hello", string.Concat(await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token)));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the turn after the refused setup never finished within ten seconds.");
        }
    }

    [Fact]
    public async Task AnErrorFrame_KeepsTheSocketOpen()
    {
        // This frame reports our defect, not a call fault. Dropping the call would be worse.
        using FragmentingChatClient reply = new("hello");
        await using var host = await TelnyxRelayHost.StartAsync(PolicyYaml, reply);
        await using var relay = await host.ConnectAsync();

        await relay.SendAsync(RelayFrames.Setup());
        await relay.SendAsync(RelayFrames.Error("Invalid message: missing required field: token"));
        await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

        Assert.Equal("hello", string.Concat(await relay.ReadTextFramesUntilLastAsync()));
    }

    [Fact(Timeout = 30_000)]
    public async Task ASecondSetupFrame_ReplacesTheFirstSessionAndReleasesIt()
    {
        // One socket carries one call, so a second setup frame is the vendor's defect. Section 7.1
        // still forbids dropping a call over one, so it replaces rather than refuses. Teardown only
        // ever closes the session the connection currently holds, so a first session left behind
        // would never have anything wait for the words it still owed store 1.
        using FragmentingChatClient reply = new("hello");
        EventObservedLoggerProvider capture = new("SecondSetupFrame");
        await using var host = await TelnyxRelayHost.StartAsync(
            PolicyYaml,
            reply,
            logging: logging => logging.AddProvider(capture));
        await using var relay = await host.ConnectAsync();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        await relay.SendAsync(RelayFrames.Setup(callSessionId: "call-first"));
        await host.WaitForSessionAsync("call-first");

        await relay.SendAsync(RelayFrames.Setup(callSessionId: "call-second"));

        try
        {
            await capture.Observed.WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the connection never reported the second setup frame within ten seconds.");
        }

        await host.WaitForSessionAsync("call-second");
        Assert.Null(await host.FindSessionAsync("call-first"));

        // The call itself goes on, on the session the vendor's latest word named.
        await relay.SendAsync(RelayFrames.Prompt("hi", last: true));

        try
        {
            Assert.Equal("hello", string.Concat(await relay.ReadTextFramesUntilLastAsync().WaitAsync(bounded.Token)));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the turn after the second setup frame never finished within ten seconds.");
        }
    }

    // -------------------------------------------------------------------------------------------
    // What teardown does.
    // -------------------------------------------------------------------------------------------

    [Fact(Timeout = 30_000)]
    public async Task ARelayThatVanishesWithNoCloseFrame_ReleasesTheSession()
    {
        // The vendor never reconnects, so a dead socket is a finished call. Abort sends no close
        // frame at all, so this is what proves the read loop unblocks on the connection's own
        // cancellation rather than on the close handshake, and that the session store drops the
        // call once it does. WaitForSessionAsync first makes sure the server actually created the
        // session before the abort below, so a pass here proves removal and not merely the absence
        // of something that was never added.
        using FragmentingChatClient reply = new("hello");
        await using var host = await TelnyxRelayHost.StartAsync(PolicyYaml, reply);
        await using var relay = await host.ConnectAsync();

        await relay.SendAsync(RelayFrames.Setup(callSessionId: "call-dropped"));
        await host.WaitForSessionAsync("call-dropped");

        relay.Abort();

        await host.WaitForCallEndAsync("call-dropped");
    }
}

/// <summary>
/// Completes once the connection writes the "the caller pressed a key" line.
/// </summary>
/// <remarks>
/// Matched by <see cref="EventId.Name"/> rather than its numeric id, because the numeric id is not
/// a value a test should depend on. This is the only signal a dtmf frame leaves behind — the digit
/// itself never reaches a log line — so it is also the only way a test can prove the connection
/// read the frame at all.
/// </remarks>
internal sealed class DtmfObservedLoggerProvider : ILoggerProvider
{
    private readonly TaskCompletionSource _observed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets a task that completes once the connection logs that a digit arrived.</summary>
    public Task Observed => _observed.Task;

    public ILogger CreateLogger(string categoryName) => new Logger(_observed);

    public void Dispose()
    {
        // Nothing to release.
    }

    private sealed class Logger(TaskCompletionSource observed) : ILogger
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
            if (eventId.Name == "DtmfReceived")
            {
                observed.TrySetResult();
            }
        }
    }
}

using System.Runtime.CompilerServices;
using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// What the transcript and the reply text hold once a barge-in cuts a turn off. Item 6a of section
/// 11: the record holds the text the caller heard, and a side effect that already ran is not dropped.
/// </summary>
/// <remarks>
/// Every test here runs offline. There is no network call and no API key anywhere in this file.
/// </remarks>
public sealed class CallSessionInterruptionTests
{
    private const string NoToolYaml =
        """
        apiVersion: agentcore/v1
        name: one-agent
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        """;

    private const string ToolYaml =
        """
        apiVersion: agentcore/v1
        name: one-agent-with-tool
        tools:
          - { id: price_lookup, kind: builtin, uses: orders.read }
        agents:
          items:
            - { id: only, instructions: "quote the price", tools: [ price_lookup ] }
        """;

    private const string ParallelToolYaml =
        """
        apiVersion: agentcore/v1
        name: one-agent-with-parallel-tool
        tools:
          - { id: quote, kind: builtin, uses: orders.read }
        agents:
          items:
            - { id: only, instructions: "quote both items", tools: [ quote ] }
        """;

    private const string ExtractorYaml =
        """
        apiVersion: agentcore/v1
        name: one-agent-with-extractor
        state:
          callerName:
            type: string
            writer: extractor
            description: the name the caller gave
        extractor:
          model: { ref: fill }
          when: after_reply
        agents:
          items:
            - { id: only, instructions: "greet the caller" }
        """;

    // -------------------------------------------------------------------------------------------
    // What the transcript holds after the caller cuts the reply off.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABargeInBeforeTheFirstWord_AddsNoAssistantMessage()
    {
        // The relay reports an empty heard text when the caller speaks over the greeting at once.
        await using var fixture = InterruptionFixture.Start(reply: "one two three four five");

        var turn = await fixture.InterruptAfterFirstUpdateAsync(heard: string.Empty);

        Assert.Equal(string.Empty, turn.ReplyText);
        Assert.DoesNotContain(fixture.Session.Transcript, m => m.Role == ChatRole.Assistant);
    }

    [Fact]
    public async Task TheHeardText_IsStoredTrimmed()
    {
        // The model streams "Hello ", and the caller heard "Hello". A trailing space is not speech.
        await using var fixture = InterruptionFixture.Start(reply: "Hello there");

        var turn = await fixture.InterruptAfterFirstUpdateAsync(heard: "Hello ");

        Assert.Equal("Hello", turn.ReplyText);
        var last = Assert.Single(fixture.Session.Transcript, m => m.Role == ChatRole.Assistant);
        Assert.Equal("Hello", last.Text);
    }

    [Fact]
    public async Task AToolCallThatFinishedBeforeTheBargeIn_StaysInTheTranscript()
    {
        // The side effect ran, so the next turn must see it. Only an unpaired call is dropped.
        await using var fixture = InterruptionFixture.StartWithFinishedTool(reply: "the price is fifty");

        await fixture.InterruptAfterFirstUpdateAsync(heard: "the price");

        Assert.Contains(
            fixture.Session.Transcript,
            m => m.Contents.OfType<FunctionCallContent>().Any());
        Assert.Contains(
            fixture.Session.Transcript,
            m => m.Contents.OfType<FunctionResultContent>().Any());
    }

    [Fact]
    public async Task AParallelToolRound_KeepsTheCallThatFinishedAndDropsTheOneThatDidNot()
    {
        // A parallel round can pair one call and leave a sibling call, in the same message, mid-
        // flight. The rule is per call id, so only the unfinished call is dropped.
        using ParallelToolCallChatClient reply = new();
        PartiallyAnsweredToolFactory tools = new();
        var session = CreateSession(ParallelToolYaml, reply, tools);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token, TestContext.Current.CancellationToken);

        // RunTurnAsync discards the whole response on cancellation (CallSession.cs, the tool-fault
        // catch above CompleteTurnAsync), so only the streaming entry point keeps the round that
        // already finished. That is the shape a relay actually drives.
        var pump = Task.Run(
            async () =>
            {
                await foreach (var _ in session.RunTurnStreamingAsync("price both items", linked.Token)
                    .ConfigureAwait(false))
                {
                    // Neither fact of this test reads an update; it reads the finished transcript.
                }
            },
            CancellationToken.None);

        // The first call already answered by the time the second call is blocked in flight.
        await tools.SecondCallStarted.Task.WaitAsync(linked.Token);
        Assert.True(session.Interrupt("the first one is", TimeSpan.FromMilliseconds(1820)));

        await pump;

        Assert.DoesNotContain(
            session.Transcript,
            m => m.Contents.OfType<FunctionCallContent>().Any(call => call.CallId == ParallelToolCallChatClient.SecondCallId));

        // Round one's own message, holding only the finished call, survives either way and is not
        // what pins the defect. What pins it is round two's message: a per-message rule drops it
        // whole because it also carries the unfinished call, so the finished call only ever shows up
        // once (from round one). The per-call-id rule instead trims round two down to the finished
        // call and keeps it, so the finished call shows up twice.
        var callAMessages = session.Transcript
            .Where(m => m.Contents.OfType<FunctionCallContent>().Any(call => call.CallId == ParallelToolCallChatClient.FirstCallId))
            .ToList();
        Assert.Equal(2, callAMessages.Count);

        var trimmedRoundTwo = callAMessages[^1];
        Assert.Equal(
            ParallelToolCallChatClient.FirstCallId,
            Assert.Single(trimmedRoundTwo.Contents.OfType<FunctionCallContent>()).CallId);
    }

    [Fact(Timeout = 30_000)]
    public async Task ProseBesideAFinishedToolCall_LeavesTheTranscriptOnTheHeardTextAlone()
    {
        // A real model routinely puts a line of prose and the tool call it announces in one
        // assistant message. The heard text replaces the prose, so keeping both would put the
        // reply into the transcript twice — once as the words the model produced and once as the
        // words the caller actually heard. Item 6a: the record holds what the caller heard.
        await using var fixture = InterruptionFixture.StartWithProseBesideTool(reply: "the price is fifty");

        await fixture.InterruptAfterFirstUpdateAsync(heard: "the price");

        // The side effect ran, so the pair stays. That is the rule this fact must not break.
        Assert.Contains(fixture.Session.Transcript, m => m.Contents.OfType<FunctionCallContent>().Any());
        Assert.Contains(fixture.Session.Transcript, m => m.Contents.OfType<FunctionResultContent>().Any());

        Assert.DoesNotContain(
            fixture.Session.Transcript.SelectMany(m => m.Contents).OfType<TextContent>(),
            text => text.Text.Contains(ProseBesideToolChatClient.Prose, StringComparison.Ordinal));

        var heard = Assert.Single(
            fixture.Session.Transcript,
            m => m.Role == ChatRole.Assistant && m.Contents.All(content => content is TextContent));
        Assert.Equal("the price", heard.Text);
    }

    // -------------------------------------------------------------------------------------------
    // A barge-in that arrives after the turn task already ended. Telnyx paces the audio, so the
    // model finishes streaming long before the vendor finishes speaking, and this is the common
    // shape on a real call rather than an edge case.
    // -------------------------------------------------------------------------------------------

    [Fact(Timeout = 30_000)]
    public async Task AnInterruptAfterTheTurnEnded_AmendsTheFinishedTurn()
    {
        // No gate and no blocking client: the whole point is that the turn is already over when
        // the frame lands. D28 and item 6a still hold — both vendor values pass through unchanged.
        using ScriptedChatClient reply = new("Hello", " there", " caller");
        InMemoryAuditSink sink = new();
        var session = CreateSession(NoToolYaml, reply, auditSink: sink);

        await foreach (var _ in session.RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken)
            .ConfigureAwait(false))
        {
            // The turn runs to its end. Nothing here interrupts it.
        }

        Assert.NotNull(session.LastTurn);
        Assert.Null(session.LastTurn!.InterruptedAfter);

        Assert.True(session.Interrupt("Hello there", TimeSpan.FromMilliseconds(1820)));

        Assert.Equal("Hello there", session.LastTurn!.ReplyText);
        Assert.Equal(TimeSpan.FromMilliseconds(1820), session.LastTurn.InterruptedAfter);

        // The transcript holds what the caller heard, not what the model produced.
        var assistant = Assert.Single(session.Transcript, m => m.Role == ChatRole.Assistant);
        Assert.Equal("Hello there", assistant.Text);

        // T23: the chain is append-only, so the correction is a second event that names the first.
        var events = sink.EventsOf(session.CallId);
        var completed = Assert.Single(events, item => item.Kind == AuditEventKind.TurnCompleted);
        var amendment = Assert.Single(events, item => item.Kind == AuditEventKind.ReplyInterrupted);
        Assert.Equal(completed.Sequence, amendment.AmendsSequence);
        Assert.Equal(completed.TurnIndex, amendment.TurnIndex);
        Assert.Equal("Hello there", amendment.Payload[AuditPayloadKeys.UtteranceUntilInterrupt]);
        Assert.Equal("1820", amendment.Payload[AuditPayloadKeys.DurationUntilInterruptMs]);

        // One barge-in cuts one reply once. A repeat frame amends nothing a second time.
        Assert.False(session.Interrupt("Hello there caller", TimeSpan.FromMilliseconds(2400)));
        Assert.Single(sink.EventsOf(session.CallId), item => item.Kind == AuditEventKind.ReplyInterrupted);
    }

    [Fact(Timeout = 30_000)]
    public void AnInterruptBeforeAnyTurnEverRan_ChangesNothingAndThrowsNothing()
    {
        using ScriptedChatClient reply = new("hello");
        InMemoryAuditSink sink = new();
        var session = CreateSession(NoToolYaml, reply, auditSink: sink);

        Assert.False(session.Interrupt("nothing played", TimeSpan.FromMilliseconds(10)));

        Assert.Null(session.LastTurn);
        Assert.Empty(session.Transcript);
        Assert.DoesNotContain(
            sink.EventsOf(session.CallId),
            item => item.Kind == AuditEventKind.ReplyInterrupted);
    }

    [Fact(Timeout = 30_000)]
    public async Task AnInterruptWhileAnUnheardSecondTurnRuns_AmendsTheFirstTurnAndLetsTheSecondSpeak()
    {
        // The held prompt of item 6a starts turn two inside turn one's own finally, while the
        // vendor is still speaking turn one. The barge-in belongs to the turn the caller was
        // hearing, so turn two must not be cut off before it has said a word.
        using SecondTurnGatedChatClient reply = new("first reply", "second reply");
        InMemoryAuditSink sink = new();
        var session = CreateSession(NoToolYaml, reply, auditSink: sink);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        await foreach (var _ in session.RunTurnStreamingAsync("one", bounded.Token).ConfigureAwait(false))
        {
            // Turn one runs to its end, exactly as it does on a real call.
        }

        var first = session.LastTurn;
        Assert.NotNull(first);

        List<string> spoken = [];
        var second = Task.Run(
            async () =>
            {
                await foreach (var update in session.RunTurnStreamingAsync("two", bounded.Token)
                    .ConfigureAwait(false))
                {
                    spoken.Add(update.Text);
                }
            },
            CancellationToken.None);

        try
        {
            try
            {
                await reply.SecondTurnStarted.Task.WaitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the second turn never started within ten seconds.");
            }

            Assert.True(session.Interrupt("first", TimeSpan.FromMilliseconds(1820)));
        }
        finally
        {
            // The gate ignores cancellation, so a failed assertion above must still release it.
            reply.OpenGate();
        }

        try
        {
            await second.WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("the second turn never finished within ten seconds.");
        }

        // The second turn spoke every word it had. Nothing silenced it.
        Assert.Equal("second reply", string.Concat(spoken));
        Assert.Null(session.LastTurn!.InterruptedAfter);

        var amendment = Assert.Single(
            sink.EventsOf(session.CallId),
            item => item.Kind == AuditEventKind.ReplyInterrupted);
        Assert.Equal(first!.TurnIndex, amendment.TurnIndex);
        Assert.Equal("first", amendment.Payload[AuditPayloadKeys.UtteranceUntilInterrupt]);
    }

    // -------------------------------------------------------------------------------------------
    // A barge-in that lands while the turn is still finishing itself. The reply is over, the writers
    // are running, and the extractor can hold the turn for up to TurnCompletionTimeout — five whole
    // seconds in which the run still looks live to Interrupt.
    // -------------------------------------------------------------------------------------------

    [Fact(Timeout = 30_000)]
    public async Task AnInterruptWhileTheExtractorRuns_IsRecordedAndNotOnlyReported()
    {
        // CompleteTurnAsync reads the interruption once, before it awaits the extractor. EndRun runs
        // later still, in the finally of whichever method opened the turn, so a frame that lands
        // inside that await finds a live run and an audible one: Interrupt takes the in-flight path,
        // sets the record, and answers true — and true, on its own contract, means "this call
        // recorded the barge-in". The commit lock reads the field a second time for exactly this
        // frame. Without that read the turn commits as an ordinary completed turn: InterruptedAfter
        // stays null, the transcript keeps the whole reply the model produced rather than the words
        // the caller heard, and the chain carries no reply.interrupted event at all.
        using ScriptedChatClient reply = new("Hello", " there", " caller");
        using GatedExtractorChatClient extractor = new();
        var chatClients = new RoutingChatClientFactory(reply).Route("fill", extractor);

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(ExtractorYaml), new AgentCompilationContext(chatClients));
        InMemoryAuditSink sink = new();
        var session = new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            CallSessionFactory.CreateExtractor(compiled, chatClients),
            observers: CallObservers.Standard(sink, logger: null)).Create();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, TestContext.Current.CancellationToken);

        var running = session.RunTurnAsync("hi", bounded.Token);

        try
        {
            try
            {
                await extractor.Started.Task.WaitAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Assert.Fail("the extractor never started within ten seconds.");
            }

            // The reply is complete and the turn is inside the extractor await. Interrupt says it
            // recorded this, so the rest of this test holds it to that answer.
            Assert.True(session.Interrupt("Hello there", TimeSpan.FromMilliseconds(1820)));
        }
        finally
        {
            // The gate ignores cancellation, so a failed assertion above must still release it
            // rather than leave the turn parked until TurnCompletionTimeout expires.
            extractor.OpenGate();
        }

        var turn = await running.WaitAsync(bounded.Token);

        // D28: both values are the ones the relay reported, unchanged.
        Assert.Equal(TimeSpan.FromMilliseconds(1820), turn.InterruptedAfter);
        Assert.Equal("Hello there", turn.ReplyText);
        Assert.Same(turn, session.LastTurn);

        // The transcript holds what the caller heard, and never the tail the model produced.
        var assistant = Assert.Single(session.Transcript, message => message.Role == ChatRole.Assistant);
        Assert.Equal("Hello there", assistant.Text);

        // T23: the correction is a second event that names the first, and never an edit of it.
        var events = sink.EventsOf(session.CallId);
        var completed = Assert.Single(events, item => item.Kind == AuditEventKind.TurnCompleted);
        var amendment = Assert.Single(events, item => item.Kind == AuditEventKind.ReplyInterrupted);
        Assert.Equal(completed.Sequence, amendment.AmendsSequence);
        Assert.Equal(completed.TurnIndex, amendment.TurnIndex);
        Assert.Equal("Hello there", amendment.Payload[AuditPayloadKeys.UtteranceUntilInterrupt]);
        Assert.Equal("1820", amendment.Payload[AuditPayloadKeys.DurationUntilInterruptMs]);

        // One barge-in cuts one reply once. The turn is no longer amendable, so a repeat says so.
        Assert.False(session.Interrupt("Hello there caller", TimeSpan.FromMilliseconds(2400)));
        Assert.Single(sink.EventsOf(session.CallId), item => item.Kind == AuditEventKind.ReplyInterrupted);
    }

    // -------------------------------------------------------------------------------------------
    // The deadline that bounds the work after the reply.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnExtractorThatNeverReturns_DoesNotHoldTheCallOpenForever()
    {
        // The extractor runs on the host token on purpose, so a barge-in never cancels it. Nothing
        // else bounds it, and a hung extractor would hold the call. livekit carries a five-second
        // watchdog for the same reason.
        await using var fixture = InterruptionFixture.StartWithHangingExtractor(reply: "hello");

        var turn = await fixture.RunTurnAsync("hi").WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.NotNull(turn);
        Assert.Equal(CallSession.ExtractionTimedOutReason, fixture.LastExtractionFailure);
    }

    // -------------------------------------------------------------------------------------------
    // Helpers.
    // -------------------------------------------------------------------------------------------

    private static CallSession CreateSession(
        string yaml,
        IChatClient reply,
        IAgentToolFactory? tools = null,
        IAuditSinkPort? auditSink = null)
    {
        // There is always a sink now: CallObservers.Standard takes a required one, because the
        // composition root resolves providers.audit for every host and falls back to the in-process
        // memory kind. An optional parameter has to be a compile-time constant, so the default is
        // spelled here instead — a fact that does not care where its events land gets a fresh
        // in-memory sink and reads exactly as it did when it passed nothing.
        IAuditSinkPort sink = auditSink ?? new InMemoryAuditSink();

        var document = ConfigurationLoader.LoadYaml(yaml);
        var chatClients = new FakeChatClientFactory(reply);
        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients) { Tools = tools });

        var factory = new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            CallSessionFactory.CreateExtractor(compiled, chatClients),
            observers: CallObservers.Standard(sink, logger: null));

        return factory.Create();
    }

    /// <summary>
    /// Drives one streaming turn on a background task, far enough to interrupt it.
    /// </summary>
    /// <remarks>
    /// Every fact of this file wants the same shape: a model that streams its first piece of content,
    /// then holds until the test releases it. This fixture builds that turn once, over
    /// <see cref="ScriptedChatClient"/> and <see cref="GatedToolThenReplyChatClient"/>, so each fact
    /// reads as one line of setup and one assertion of what the interruption left behind.
    /// </remarks>
    private sealed class InterruptionFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly IReadOnlyList<IDisposable> _disposables;
        private readonly Action _openGate;
        private readonly TaskCompletionSource _firstUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _run;

        private InterruptionFixture(CallSession session, IChatClient reply, Action openGate, CancellationToken hostToken)
        {
            Session = session;
            _disposables = [reply];
            _openGate = openGate;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            _cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            // The run reads its own token, _cancellation.Token, from inside PumpAsync, so the token
            // Task.Run offers here is deliberately unused.
            _run = Task.Run(PumpAsync, CancellationToken.None);
        }

        /// <summary>Builds a fixture over a turn that runs directly, with no streaming pump behind it.</summary>
        private InterruptionFixture(CallSession session, IReadOnlyList<IDisposable> disposables, CancellationToken hostToken)
        {
            Session = session;
            _disposables = disposables;
            _openGate = static () => { };
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            _cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            // RunTurnAsync drives the turn itself, on the caller's own call. There is no round to
            // stream and gate, so there is nothing here for a background pump to do.
            _run = Task.CompletedTask;
        }

        /// <summary>Gets the session the turn runs on.</summary>
        public CallSession Session { get; }

        /// <summary>Starts a turn over a plain text reply, gated after its first fragment.</summary>
        public static InterruptionFixture Start(string reply)
        {
            ScriptedChatClient client = new(reply.Split(' ')) { GateAfterFirstFragment = true };
            var session = CreateSession(NoToolYaml, client);
            return new InterruptionFixture(session, client, client.OpenGate, TestContext.Current.CancellationToken);
        }

        /// <summary>Starts a turn whose tool call and result finish before the reply that follows them.</summary>
        public static InterruptionFixture StartWithFinishedTool(string reply)
        {
            GatedToolThenReplyChatClient client = new(reply);
            var session = CreateSession(ToolYaml, client, new StubToolFactory("""{ "price": 50 }"""));
            return new InterruptionFixture(session, client, client.OpenGate, TestContext.Current.CancellationToken);
        }

        /// <summary>Starts a turn whose tool round also carries the prose the model spoke before the call.</summary>
        public static InterruptionFixture StartWithProseBesideTool(string reply)
        {
            ProseBesideToolChatClient client = new(reply);
            var session = CreateSession(ToolYaml, client, new StubToolFactory("""{ "price": 50 }"""));
            return new InterruptionFixture(session, client, client.OpenGate, TestContext.Current.CancellationToken);
        }

        /// <summary>Builds a session whose extractor model never answers, so the deadline of §8.7 must act.</summary>
        /// <remarks>
        /// The extractor runs on <c>fill</c>, the route <see cref="RoutingChatClientFactory"/> gives a
        /// test to script apart from the reply model. <see cref="HangingChatClient"/> answers that
        /// route, and it awaits a <see cref="TaskCompletionSource"/> nobody ever sets, so nothing but
        /// <see cref="CallSession.TurnCompletionTimeout"/> ends the call.
        /// </remarks>
        public static InterruptionFixture StartWithHangingExtractor(string reply)
        {
            ScriptedChatClient replyClient = new(reply.Split(' '));
            HangingChatClient extractorClient = new();
            var chatClients = new RoutingChatClientFactory(replyClient).Route("fill", extractorClient);

            var document = ConfigurationLoader.LoadYaml(ExtractorYaml);
            var compiled = ConfigurationCompiler.Compile(document, new AgentCompilationContext(chatClients));
            var factory = new CallSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                CallSessionFactory.CreateExtractor(compiled, chatClients));

            return new InterruptionFixture(
                factory.Create(),
                [replyClient, extractorClient],
                TestContext.Current.CancellationToken);
        }

        /// <summary>Runs one turn directly on the session, the way a host that never streams drives it.</summary>
        public Task<TurnResult> RunTurnAsync(string userInput) => Session.RunTurnAsync(userInput, _cancellation.Token);

        /// <summary>Gets the extraction failure the last finished turn recorded, or <see langword="null"/>.</summary>
        public string? LastExtractionFailure => Session.LastTurn?.ExtractionFailure;

        /// <summary>Waits for the first update, interrupts with what the caller heard, and returns the finished turn.</summary>
        public async Task<TurnResult> InterruptAfterFirstUpdateAsync(string heard)
        {
            await _firstUpdate.Task.WaitAsync(_cancellation.Token).ConfigureAwait(false);

            // Section 7.1: the relay reports both the heard text and the played duration together.
            Session.Interrupt(heard, TimeSpan.FromMilliseconds(1820));
            _openGate();

            await _run.WaitAsync(_cancellation.Token).ConfigureAwait(false);
            Assert.NotNull(Session.LastTurn);
            return Session.LastTurn;
        }

        public async ValueTask DisposeAsync()
        {
            // A fact that stops before it interrupts must not leave the background task hanging.
            _openGate();
            _firstUpdate.TrySetResult();

            try
            {
                await _run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The host token already ended the run. Nothing here is left to observe.
            }

            _cancellation.Dispose();
            foreach (var disposable in _disposables)
            {
                disposable.Dispose();
            }
        }

        private async Task PumpAsync()
        {
            await foreach (var update in Session.RunTurnStreamingAsync("hi", _cancellation.Token)
                .ConfigureAwait(false))
            {
                // The tool-call and tool-result updates carry no TextContent, so only the reply
                // itself opens the window in which a test may interrupt.
                if (update.Contents.OfType<TextContent>().Any(text => text.Text.Length > 0))
                {
                    _firstUpdate.TrySetResult();
                }
            }
        }
    }

    /// <summary>
    /// Calls the one tool it is offered, then streams its final reply and blocks after the first
    /// fragment until the test opens the gate.
    /// </summary>
    /// <remarks>
    /// <see cref="ToolCallingChatClient"/> covers the tool round, and <see cref="ScriptedChatClient"/>
    /// covers a gated round, but the finished-tool fact of this file needs both in one run: a tool
    /// call whose result already arrived before the barge-in reaches the reply that follows it.
    /// </remarks>
    private sealed class GatedToolThenReplyChatClient : IChatClient
    {
        private const string ToolCallId = "call_1";

        private readonly string[] _fragments;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedToolThenReplyChatClient(string reply) => _fragments = reply.Split(' ');

        /// <summary>Lets every fragment after the first flow.</summary>
        public void OpenGate() => _gate.TrySetResult();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await Task.Yield();

            var answered = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());
            var responseId = Guid.NewGuid().ToString("N");

            if (!answered && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent(ToolCallId, tool.Name, new Dictionary<string, object?>(StringComparer.Ordinal))])
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };

                yield break;
            }

            for (var index = 0; index < _fragments.Length; index++)
            {
                if (index == 1)
                {
                    // The tool round already finished, so this is the round a barge-in interrupts.
                    await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                var text = index == 0 ? _fragments[index] : " " + _fragments[index];
                yield return new ChatResponseUpdate(ChatRole.Assistant, text)
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
            }
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>
    /// Announces the tool in prose and calls it, both on one assistant message, then streams its
    /// final reply and blocks after the first fragment until the test opens the gate.
    /// </summary>
    /// <remarks>
    /// <see cref="GatedToolThenReplyChatClient"/> yields a tool round that carries nothing but the
    /// call. A real model routinely writes a line first — "Let me check that for you" — and puts the
    /// call beside it on the same message. No other fake in this project can produce that shape, and
    /// it is the shape the transcript rule of item 6a has to survive.
    /// </remarks>
    private sealed class ProseBesideToolChatClient : IChatClient
    {
        /// <summary>The line the model speaks before it calls the tool.</summary>
        public const string Prose = "Let me check that for you";

        private const string ToolCallId = "call_1";

        private readonly string[] _fragments;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProseBesideToolChatClient(string reply) => _fragments = reply.Split(' ');

        /// <summary>Lets every fragment after the first flow.</summary>
        public void OpenGate() => _gate.TrySetResult();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await Task.Yield();

            var answered = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());
            var responseId = Guid.NewGuid().ToString("N");

            if (!answered && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
            {
                // The prose and the call ride one message, which is what a real model produces.
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [
                        new TextContent(Prose),
                        new FunctionCallContent(ToolCallId, tool.Name, new Dictionary<string, object?>(StringComparer.Ordinal)),
                    ])
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };

                yield break;
            }

            for (var index = 0; index < _fragments.Length; index++)
            {
                if (index == 1)
                {
                    // The tool round already finished, so this is the round a barge-in interrupts.
                    await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                var text = index == 0 ? _fragments[index] : " " + _fragments[index];
                yield return new ChatResponseUpdate(ChatRole.Assistant, text)
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
            }
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>
    /// Streams its first reply to the end, then blocks the second one before it says a word.
    /// </summary>
    /// <remarks>
    /// This is the shape a held prompt produces: turn one finishes streaming, turn two starts at
    /// once, and the vendor is still speaking turn one. A barge-in that lands in that window belongs
    /// to turn one, and turn two has produced nothing for anyone to have heard.
    /// </remarks>
    private sealed class SecondTurnGatedChatClient : IChatClient
    {
        private readonly string _first;
        private readonly string _second;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public SecondTurnGatedChatClient(string first, string second)
        {
            _first = first;
            _second = second;
        }

        /// <summary>Signals once the second turn's own model call is in flight and blocked.</summary>
        public TaskCompletionSource SecondTurnStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets the second reply flow.</summary>
        public void OpenGate() => _gate.TrySetResult();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await Task.Yield();

            var index = Interlocked.Increment(ref _calls);
            var responseId = Guid.NewGuid().ToString("N");

            if (index > 1)
            {
                SecondTurnStarted.TrySetResult();

                // Not WaitAsync(cancellationToken): a defect that cancelled this turn would then
                // look like the pass this test is trying to disprove.
                await _gate.Task.ConfigureAwait(false);
            }

            foreach (var word in (index == 1 ? _first : _second).Split(' ')
                .Select((word, position) => position == 0 ? word : " " + word))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, word)
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
            }
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>
    /// Answers with one call, then, once that call's result is in context, answers with that same
    /// call again alongside a brand new one, both on a single message.
    /// </summary>
    /// <remarks>
    /// The runtime commits a round's tool results as one batch, so a round that never finishes
    /// invoking surfaces no message at all, finished or not. Round one is a single call and finishes
    /// on its own, so its result is already on the transcript when round two's message asks for two
    /// calls at once. That is the only way to reach the shape the multi-call fact needs: a message
    /// with two calls where the finished-tool filter must trim rather than drop or keep it whole.
    /// </remarks>
    private sealed class ParallelToolCallChatClient : IChatClient
    {
        /// <summary>The id of the call whose result arrives before the barge-in.</summary>
        public const string FirstCallId = "call_a";

        /// <summary>The id of the call still in flight when the barge-in arrives.</summary>
        public const string SecondCallId = "call_b";

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await Task.Yield();

            var answered = messages
                .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
                .Select(result => result.CallId)
                .ToHashSet(StringComparer.Ordinal);

            var tool = options?.Tools?.OfType<AIFunction>().FirstOrDefault()
                ?? throw new InvalidOperationException("The turn offers no tool to call.");
            var responseId = Guid.NewGuid().ToString("N");

            if (!answered.Contains(FirstCallId))
            {
                // Round one. One call, so the runtime's batch commits alone and finishes at once.
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            FirstCallId, tool.Name, new Dictionary<string, object?>(StringComparer.Ordinal) { ["index"] = 1 }),
                    ])
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };

                yield break;
            }

            // Round two. The already-answered call rides again beside the new one, on one message.
            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        FirstCallId, tool.Name, new Dictionary<string, object?>(StringComparer.Ordinal) { ["index"] = 1 }),
                    new FunctionCallContent(
                        SecondCallId, tool.Name, new Dictionary<string, object?>(StringComparer.Ordinal) { ["index"] = 2 }),
                ])
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>
    /// Answers the first call to the one declared tool at once, and blocks the second call until the
    /// run's own cancellation ends it.
    /// </summary>
    /// <remarks>
    /// The barge-in cancels the run's token, and nothing else ever completes the second call, so its
    /// result never joins <see cref="AgentResponse.Messages"/>. <see cref="SecondCallStarted"/> tells
    /// a test the first call already finished and the second is now the one in flight, which is the
    /// instant a test must interrupt to pin the per-call-id rule.
    /// </remarks>
    private sealed class PartiallyAnsweredToolFactory : IAgentToolFactory
    {
        /// <summary>Signals once the second call is in flight and blocked.</summary>
        public TaskCompletionSource SecondCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AITool? Create(ToolConfiguration tool)
        {
            ArgumentNullException.ThrowIfNull(tool);
            return AIFunctionFactory.Create(AnswerAsync, tool.Id, tool.Description ?? tool.Id);
        }

        private async Task<string> AnswerAsync(int index, CancellationToken cancellationToken)
        {
            if (index == 1)
            {
                return "50";
            }

            SecondCallStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            // Unreachable: the delay above only ever ends in cancellation.
            return string.Empty;
        }
    }

    /// <summary>
    /// Reports that the extractor call is in flight, then holds it until the test lets it answer.
    /// </summary>
    /// <remarks>
    /// <see cref="HangingChatClient"/> never answers at all, which is what §8.7's deadline needs.
    /// This one has to answer, because the fact it serves is about what the turn commits *after* the
    /// extractor returns: the window a late barge-in lands in is exactly the width of this call, and
    /// nothing else in the turn loop opens one that a test can stand inside of. The gate is not read
    /// through <see cref="CancellationToken"/> on purpose — the extractor runs on the host token so
    /// a barge-in never cancels it, and a defect that did cancel it would end this wait quietly and
    /// look like the pass this fake exists to disprove.
    /// </remarks>
    private sealed class GatedExtractorChatClient : IChatClient
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task that completes once the extractor's own model call is in flight.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets the extractor answer.</summary>
        public void OpenGate() => _gate.TrySetResult();

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            Started.TrySetResult();
            await _gate.Task.ConfigureAwait(false);

            // An empty document fills no slot, which is all this fake owes the writer order.
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // StateExtractor.ExtractAsync calls GetResponseAsync and never streams.
            throw new NotSupportedException("The extractor calls GetResponseAsync, and never streams.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>Answers nothing, ever. It stands in for the extractor model hanging past its deadline.</summary>
    /// <remarks>
    /// §8.7's deadline is what ends this call, not the model, so <see cref="GetResponseAsync"/> awaits
    /// a <see cref="TaskCompletionSource"/> nobody ever sets. It returns only when its own
    /// <see cref="CancellationToken"/> cancels.
    /// </remarks>
    private sealed class HangingChatClient : IChatClient
    {
        private readonly TaskCompletionSource _neverSet = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await _neverSet.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            // Unreachable: nothing ever completes _neverSet, so the wait above only ever cancels.
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // StateExtractor.ExtractAsync calls GetResponseAsync and never streams.
            throw new NotSupportedException("The extractor calls GetResponseAsync, and never streams.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}

using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Tests.Audit;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn.Lifecycle;
using static AgentCore.Application.Tests.Runtime.InterruptionSessions;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// What the transcript and the reply text hold once a barge-in cuts a turn off: the record holds the text the
    /// caller heard, and a side effect that already ran is not dropped.
    /// </summary>
    public sealed class ConversationSessionInterruptionTests
    {
        // What the transcript holds after the caller cuts the reply off.

        [Fact]
        public async Task ABargeInBeforeTheFirstWord_AddsNoAssistantMessage()
        {
            // The relay reports an empty heard text when the caller speaks over the greeting at once.
            await using InterruptionFixture fixture = InterruptionFixture.Start(reply: "one two three four five");

            TurnResult turn = await fixture.InterruptAfterFirstUpdateAsync(heard: string.Empty);

            Assert.Equal(string.Empty, turn.ReplyText);
            Assert.DoesNotContain(fixture.Session.Transcript, m => m.Role == ChatRole.Assistant);
        }

        [Fact]
        public async Task TheHeardText_IsStoredTrimmed()
        {
            // The model streams "Hello ", and the caller heard "Hello". A trailing space is not speech.
            await using InterruptionFixture fixture = InterruptionFixture.Start(reply: "Hello there");

            TurnResult turn = await fixture.InterruptAfterFirstUpdateAsync(heard: "Hello ");

            Assert.Equal("Hello", turn.ReplyText);
            ChatMessage last = Assert.Single(fixture.Session.Transcript, m => m.Role == ChatRole.Assistant);
            Assert.Equal("Hello", last.Text);
        }

        [Fact]
        public async Task AToolCallThatFinishedBeforeTheBargeIn_StaysInTheTranscript()
        {
            // The side effect ran, so the next turn must see it. Only an unpaired call is dropped.
            await using InterruptionFixture fixture = InterruptionFixture.StartWithFinishedTool(reply: "the price is fifty");

            _ = await fixture.InterruptAfterFirstUpdateAsync(heard: "the price");

            Assert.Contains(
                fixture.Session.Transcript,
                m => m.Contents.OfType<FunctionCallContent>().Any());
            Assert.Contains(
                fixture.Session.Transcript,
                m => m.Contents.OfType<FunctionResultContent>().Any());
        }

        [Fact(Timeout = 30_000)]
        public async Task AParallelToolRound_KeepsTheCallThatFinishedAndTheOneStillRunningAtTheCut()
        {
            // As in LiveKit, a cut lets the call still in flight finish, and keeps its call and result, so the next
            // turn does not run it again.
            using ParallelToolCallChatClient reply = new();
            PartiallyAnsweredToolFactory tools = new();
            ConversationSession session = CreateSession(ParallelToolYaml, reply, tools.Create);

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token, TestContext.Current.CancellationToken);

            // The streaming shape is the one a relay drives.
            Task pump = Task.Run(
                async () =>
                {
                    await foreach (ChatResponseUpdate? _ in session.RunTurnStreamingAsync("price both items", linked.Token)
                        .ConfigureAwait(false))
                    {
                        // Neither fact of this test reads an update; it reads the finished transcript.
                    }
                },
                CancellationToken.None);

            // The first call already answered by the time the second call is blocked in flight.
            await tools.SecondConversationStarted.Task.WaitAsync(linked.Token);
            Assert.True(session.Cut(0, new TurnCut("the first one is", TimeSpan.FromMilliseconds(1820))));
            await pump;
            tools.ReleaseSecond.SetResult();
            await session.ToolRuns.WhenIdle();

            // The cut turn keeps the finished call; the one in flight at the cut is stored, call then result, once it finished.
            List<ChatMessage> transcript = [.. session.Transcript];
            Assert.Contains(transcript.SelectMany(m => m.Contents), content => content is FunctionResultContent { CallId: ParallelToolCallChatClient.FirstCallId });
            int call = transcript.FindIndex(m => m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == ParallelToolCallChatClient.SecondCallId));
            FunctionResultContent second = Assert.Single(transcript[call + 1].Contents.OfType<FunctionResultContent>());
            Assert.Equal((ParallelToolCallChatClient.SecondCallId, PartiallyAnsweredToolFactory.SecondAnswer), (second.CallId, second.Result?.ToString()));
        }

        [Fact(Timeout = 30_000)]
        public async Task ProseBesideAFinishedToolCall_KeepsTheProseInPlaceAndCutsTheLastStep()
        {
            // A real model routinely puts a line of prose and the tool call it announces in one
            // assistant message, then answers in a second step. The caller heard the prose whole and
            // part of the answer, so the prose stays before its call and only the answer is cut
            // (the cut names every word heard, across steps).
            await using InterruptionFixture fixture = InterruptionFixture.StartWithProseBesideTool(reply: "the price is fifty");

            _ = await fixture.InterruptAfterFirstUpdateAsync(heard: ProseBesideToolChatClient.Prose + "the price");

            Assert.Equal(
                [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant],
                fixture.Session.Transcript.Select(m => m.Role));

            ChatMessage announced = fixture.Session.Transcript[1];
            Assert.Equal(ProseBesideToolChatClient.Prose, announced.Text);
            _ = Assert.Single(announced.Contents.OfType<FunctionCallContent>());
            _ = Assert.Single(fixture.Session.Transcript[2].Contents.OfType<FunctionResultContent>());
            Assert.Equal("the price", fixture.Session.Transcript[3].Text);
        }

        // A barge-in that arrives after the turn task already ended. Telnyx paces the audio, so the
        // model finishes streaming long before the vendor finishes speaking, and this is the common
        // shape on a real conversation rather than an edge case.

        [Fact(Timeout = 30_000)]
        public async Task AnInterruptAfterTheTurnEnded_AmendsTheFinishedTurn()
        {
            // No gate and no blocking client: the whole point is that the turn is already over when
            // the frame lands. Both vendor values pass through unchanged.
            using ScriptedChatClient reply = new("Hello", " there", " caller");
            InMemoryAuditSink sink = new();
            ConversationSession session = CreateSession(NoToolYaml, reply, auditSink: sink);

            await foreach (ChatResponseUpdate? _ in session.RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken)
                .ConfigureAwait(false))
            {
                // The turn runs to its end. Nothing here interrupts it.
            }

            Assert.NotNull(session.LastTurn);
            Assert.Null(session.LastTurn!.Cut);

            Assert.True(session.Cut(0, new TurnCut("Hello there", TimeSpan.FromMilliseconds(1820))));

            Assert.Equal("Hello there", session.LastTurn!.ReplyText);
            Assert.Equal(TimeSpan.FromMilliseconds(1820), session.LastTurn.Cut);

            // The transcript holds what the caller heard, not what the model produced.
            ChatMessage assistant = Assert.Single(session.Transcript, m => m.Role == ChatRole.Assistant);
            Assert.Equal("Hello there", assistant.Text);

            // The chain is append-only, so the correction is a second event that names the first.
            IReadOnlyList<AuditEvent> events = await session.RowsAsync(sink);
            AuditEvent completed = Assert.Single(events, item => item.Kind == AuditEventKind.TurnCompleted);
            AuditEvent amendment = Assert.Single(events, item => item.Kind == AuditEventKind.ReplyInterrupted);
            Assert.Equal(completed.EventId, amendment.AmendsEventId);
            Assert.Equal(completed.TurnIndex, amendment.TurnIndex);
            Assert.Equal(
                AuditHash.OfText("Hello there").Value,
                amendment.Payload[AuditPayloadKeys.UtteranceUntilInterruptSha256]);
            Assert.Equal("1820", amendment.Payload[AuditPayloadKeys.DurationUntilInterruptMs]);

            // One barge-in cuts one reply once. A repeat frame amends nothing a second time.
            Assert.False(session.Cut(0, new TurnCut("Hello there caller", TimeSpan.FromMilliseconds(2400))));
            _ = Assert.Single(await session.RowsAsync(sink), item => item.Kind == AuditEventKind.ReplyInterrupted);
        }

        [Fact(Timeout = 30_000)]
        public async Task AnInterruptBeforeAnyTurnEverRan_ChangesNothingAndThrowsNothing()
        {
            using ScriptedChatClient reply = new("hello");
            InMemoryAuditSink sink = new();
            ConversationSession session = CreateSession(NoToolYaml, reply, auditSink: sink);

            Assert.False(session.Cut(0, new TurnCut("nothing played", TimeSpan.FromMilliseconds(10))));

            Assert.Null(session.LastTurn);
            Assert.Empty(session.Transcript);
            Assert.DoesNotContain(
                await session.RowsAsync(sink),
                item => item.Kind == AuditEventKind.ReplyInterrupted);
        }

        // A cut of the running turn that names nothing shown (Cut(turn, "", null)) keeps its user message only.
        [Fact(Timeout = 30_000)]
        public async Task AnInterruptWhileAnUnheardSecondTurnRuns_CutsTheSecondTurnWithNothingShown_AndLeavesTheFirst()
        {
            using SecondTurnGatedChatClient reply = new("first reply", "second reply");
            InMemoryAuditSink sink = new();
            ConversationSession session = CreateSession(NoToolYaml, reply, auditSink: sink);

            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token, TestContext.Current.CancellationToken);

            await foreach (ChatResponseUpdate? _ in session.RunTurnStreamingAsync("one", bounded.Token).ConfigureAwait(false))
            {
                // Turn one runs to its end, exactly as it does on a real conversation.
            }

            TurnResult? first = session.LastTurn;
            Assert.NotNull(first);

            Task second = Task.Run(
                async () =>
                {
                    await foreach (ChatResponseUpdate? _ in session.RunTurnStreamingAsync("two", bounded.Token)
                        .ConfigureAwait(false))
                    {
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

                Assert.True(session.Cut(first.TurnIndex + 1, new TurnCut(string.Empty, null)));
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

            // The running turn keeps the user's words and nothing shown; the first turn is untouched.
            Assert.Equal(["one", "first reply", "two"], session.Transcript.Select(message => message.Text));
            Assert.Equal(first!.TurnIndex + 1, session.LastTurn!.TurnIndex);
            Assert.Equal(string.Empty, session.LastTurn.ReplyText);

            AuditEvent amendment = Assert.Single(
                await session.RowsAsync(sink),
                item => item.Kind == AuditEventKind.ReplyInterrupted);
            Assert.Equal(first.TurnIndex + 1, amendment.TurnIndex);
            Assert.Equal(
                AuditHash.OfText(string.Empty).Value,
                amendment.Payload[AuditPayloadKeys.UtteranceUntilInterruptSha256]);
            Assert.False(amendment.Payload.ContainsKey(AuditPayloadKeys.DurationUntilInterruptMs));
        }

        // A barge-in that lands while the turn is still finishing itself. The reply is over, the writers
        // are running, and the extractor can hold the turn for up to TurnFailureReasons.CompletionTimeout — five whole
        // seconds in which the run still looks live to Cut.

        [Fact(Timeout = 30_000)]
        public async Task AnInterruptWhileTheExtractorRuns_IsRecordedAndNotOnlyReported()
        {
            // The seal reads the cut once, before the extractor runs. EndRun runs
            // later still, in the finally of whichever method opened the turn, so a frame that lands
            // inside that await finds a live run, waits in the cut slot as a late cut, and answers
            // true. And true, on Cut's own contract, means "this conversation recorded the cut".
            // The commit takes the late cut under the turn lock for exactly this frame. Without that read the turn commits
            // as an ordinary completed turn: Cut stays null, the transcript keeps the whole
            // reply the model produced rather than the words the caller heard, and the chain carries no
            // reply.interrupted event at all.
            using ScriptedChatClient reply = new("Hello", " there", " caller");
            using GatedExtractorChatClient extractor = new();
            RoutingChatClientFactory chatClients = new RoutingChatClientFactory(reply).Route("fill", extractor);

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(ExtractorYaml), new AgentCompilationContext(chatClients))["main"];
            InMemoryAuditSink sink = new();
            ConversationSession session = new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                ConversationSessionFactory.CreateExtractor(compiled, chatClients),
                hooks: BuiltInHooks.Create(sink)).Create();

            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token, TestContext.Current.CancellationToken);

            Task running = Task.Run(
                async () =>
                {
                    await foreach (ChatResponseUpdate? _ in session.RunTurnStreamingAsync("hi", bounded.Token)
                        .ConfigureAwait(false))
                    {
                        // The reply is consumed whole; the frame lands later, inside the extractor.
                    }
                },
                CancellationToken.None);

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

                // The reply is complete and the turn is inside the extractor await. Cut says it
                // recorded this, so the rest of this test holds it to that answer.
                Assert.True(session.Cut(0, new TurnCut("Hello there", TimeSpan.FromMilliseconds(1820))));
            }
            finally
            {
                // The gate ignores cancellation, so a failed assertion above must still release it
                // rather than leave the turn parked until TurnFailureReasons.CompletionTimeout expires.
                extractor.OpenGate();
            }

            await running.WaitAsync(bounded.Token);
            TurnResult? turn = session.LastTurn;

            // Both values are the ones the relay reported, unchanged.
            Assert.NotNull(turn);
            Assert.Equal(TimeSpan.FromMilliseconds(1820), turn.Cut);
            Assert.Equal("Hello there", turn.ReplyText);

            // The transcript holds what the caller heard, and never the tail the model produced.
            ChatMessage assistant = Assert.Single(session.Transcript, message => message.Role == ChatRole.Assistant);
            Assert.Equal("Hello there", assistant.Text);

            // The correction is a second event that names the first, and never an edit of it.
            IReadOnlyList<AuditEvent> events = await session.RowsAsync(sink);
            AuditEvent completed = Assert.Single(events, item => item.Kind == AuditEventKind.TurnCompleted);
            AuditEvent amendment = Assert.Single(events, item => item.Kind == AuditEventKind.ReplyInterrupted);
            Assert.Equal(completed.EventId, amendment.AmendsEventId);
            Assert.Equal(completed.TurnIndex, amendment.TurnIndex);
            Assert.Equal(
                AuditHash.OfText("Hello there").Value,
                amendment.Payload[AuditPayloadKeys.UtteranceUntilInterruptSha256]);
            Assert.Equal("1820", amendment.Payload[AuditPayloadKeys.DurationUntilInterruptMs]);

            // One barge-in cuts one reply once. The turn is no longer amendable, so a repeat says so.
            Assert.False(session.Cut(0, new TurnCut("Hello there caller", TimeSpan.FromMilliseconds(2400))));
            _ = Assert.Single(await session.RowsAsync(sink), item => item.Kind == AuditEventKind.ReplyInterrupted);
        }

        // The deadline that bounds the work after the reply.

        [Fact]
        public async Task AnExtractorThatNeverReturns_DoesNotHoldTheConversationOpenForever()
        {
            // The extractor runs on the host token on purpose, so a barge-in never cancels it. Nothing
            // else bounds it, and a hung extractor would hold the conversation. livekit carries a five-second
            // watchdog for the same reason.
            await using InterruptionFixture fixture = InterruptionFixture.StartWithHangingExtractor(reply: "hello");

            TurnResult turn = await fixture.RunTurnAsync("hi").WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.NotNull(turn);
            Assert.Equal(TurnFailureReasons.ExtractionTimedOut, fixture.LastExtractionFailure);
        }
    }
}

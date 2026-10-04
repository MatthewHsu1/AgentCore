using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Turn.Lifecycle;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice.Session;
using AgentCore.AspNetCore.Voice.Speech;
using AgentCore.AspNetCore.Voice.Speech.Replies;
using AgentCore.AspNetCore.Voice.Turns;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>How a <see cref="PipelineReply"/> runs, cuts and ends the engine turn behind it (<see cref="EngineReplyStream"/>).</summary>
    public sealed class PipelineReplyEngineTurnTests : IAsyncDisposable
    {
        private const int SpeechTaskFaulted = 28;

        private const int CutRefused = 34;

        private const int RecutRefused = 35;

        private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

        private readonly FakeTimeProvider _time = new(Start);

        private readonly FakeConversationOutput _output = new();

        private readonly ScriptedVoicePort _port = new();

        private readonly VoiceSession _session;

        private readonly VoiceActivity _activity;

        public PipelineReplyEngineTurnTests()
        {
            _session = new VoiceSession(_output, _time, NullLogger.Instance);
            _activity = new VoiceActivity(_session, _port, CancellationToken.None);
        }

        public ValueTask DisposeAsync()
        {
            return _output.DisposeAsync();
        }

        // VoiceActivity: engine turns run one at a time, each once the previous one has ended.
        [Fact(Timeout = 30_000)]
        public async Task ASecondReply_StartsItsEngineTurnOnlyOnceTheFirstHasEnded()
        {
            SpeechHandle first = _activity.GenerateReply("First question");
            SpeechHandle second = _activity.GenerateReply("Second question");

            Assert.Equal("First question", await _port.Turn(1).Started);
            await _port.Turn(1).TextAsync("The first answer.");
            _port.Turn(1).End();
            _ = await first;

            Task<string> started = _port.Turn(2).Started;
            Assert.Same(started, await Task.WhenAny(started, second.WaitForPlayoutAsync(TestContext.Current.CancellationToken)));
            Assert.Equal("Second question", await started);
            await _port.Turn(2).TextAsync("The second answer.");
            _port.Turn(2).End();
            _ = await second;

            Assert.Null(second.Error);
            Assert.Equal(["The first answer.", "The second answer."], _output.Spoken);
        }

        // EngineReplyStream.Cut: a turn still waiting on the one before it takes the cut the moment it starts, so the
        // caller's words are kept.
        [Fact(Timeout = 30_000)]
        public async Task AReplyInterruptedBeforeItsTurnStarted_CutsThatTurnTheMomentItStarts()
        {
            SpeechHandle first = _activity.GenerateReply("First question");
            SpeechHandle second = _activity.GenerateReply("Second question");
            await _port.Turn(1).TextAsync("The first answer.");

            _ = second.Interrupt(source: InterruptionSource.UserTurn);
            _ = await second;
            _port.Turn(1).End();
            _ = await first;

            Assert.Equal("Second question", await _port.Turn(2).Started);
            Assert.Equal([(1, new TurnCut(string.Empty, null))], _port.Cuts);
            await _port.Turn(2).Finished;
        }

        // EngineReplyStream: a turn its owner cancels is not a fault (LiveKit's _on_llm_task_done skips a cancelled task).
        [Fact(Timeout = 30_000)]
        public async Task AnEngineTurnItsOwnerCancels_EndsTheSpeechWithoutAnError()
        {
            using CancellationTokenSource transport = new();
            VoiceActivity activity = new(_session, _port, transport.Token);
            SpeechHandle handle = activity.GenerateReply("Hello?");
            _ = await _port.Turn(1).Started;

            await transport.CancelAsync();
            _ = await handle;

            Assert.Null(handle.Error);
        }

        // agent_activity.py:3866-3874: a running tool keeps the agent "thinking"; "listening" would arm the away timer.
        [Fact(Timeout = 30_000)]
        public async Task AStepEndingOnAToolCall_LeavesTheAgentThinkingWhileTheToolRuns()
        {
            SpeechHandle handle = _activity.GenerateReply("Where is my order?");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Let me look that up.");
            await turn.CallAsync("1", "lookup_order");
            await Poll.UntilAsync(() => _session.HasBackgroundSpeeches);

            Assert.Equal(AgentState.Thinking, _session.AgentState);

            await turn.ResultAsync("1");
            turn.End();
            _ = await handle;
        }

        // PipelineReply: an interruption with no barge-in behind it keeps the text forwarded.
        [Fact(Timeout = 30_000)]
        public async Task AUserTurnInterruptingAReplyWhileItsToolRuns_CutsTheTurnToTheForwardedText()
        {
            SpeechHandle handle = _activity.GenerateReply("Where is my order?");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Let me look that up.");
            await turn.CallAsync("1", "lookup_order");
            await Poll.UntilAsync(() => _session.HasBackgroundSpeeches);

            _ = handle.Interrupt(source: InterruptionSource.UserTurn);
            _ = await handle;

            Assert.Equal([(0, new TurnCut("Let me look that up.", null))], _port.Cuts);
            Assert.True(turn.Finished.IsCompleted);
        }

        // PipelineReply.ForwardStepAsync: LiveKit opens a segment on the first non-empty text only, so a step with no
        // words never closes a reply on the transport.
        [Fact(Timeout = 30_000)]
        public async Task AStepWithNoWords_ClosesNoReply()
        {
            SpeechHandle handle = _activity.GenerateReply("Check my order.");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync(string.Empty);
            await turn.CallAsync("1", "lookup_order");
            await turn.ResultAsync("1");
            await turn.TextAsync("It shipped.");
            turn.End();
            _ = await handle;

            Assert.Equal(["speak:It shipped.", "complete"], _output.Log);
        }

        // EngineReplyStream.WaitForRoundAsync reads the round of this turn's own calls; a result for a call it never
        // made does not end the step.
        [Fact(Timeout = 30_000)]
        public async Task AResultForACallThisTurnNeverMade_LeavesTheStepWhole()
        {
            SpeechHandle handle = _activity.GenerateReply("Any news?");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Yes.");
            await turn.ResultAsync("an-earlier-call");
            await turn.TextAsync(" It shipped.");
            turn.End();
            _ = await handle;

            Assert.Equal(["speak:Yes.", "speak: It shipped.", "complete"], _output.Log);
        }

        // PipelineReply.EndReply: a genuine engine failure surfaces through the speech's error (LiveKit's _on_llm_task_done).
        [Fact(Timeout = 30_000)]
        public async Task AnEngineTurnThatFails_EndsTheSpeechWithItsError()
        {
            _ = await _port.StartTurnAsync(
                new ChatMessage(ChatRole.User, "a turn still running"), origin: null, TestContext.Current.CancellationToken);

            SpeechHandle handle = _activity.GenerateReply("Hello?");
            _ = await handle;

            _ = Assert.IsType<InvalidOperationException>(handle.Error);
            Assert.Empty(_output.Spoken);
        }

        // PipelineReply.EndInterruptedAsync: once the backstop cancels the speech's token, the speech ends without a fault.
        [Fact(Timeout = 30_000)]
        public async Task AnInterruptedReplyWhoseTurnOutlivesTheBackstop_EndsWithoutAFault()
        {
            RecordingLoggerFactory logs = new();
            VoiceSession session = new(_output, _time, logs.CreateLogger("voice"));
            VoiceActivity activity = new(session, _port, CancellationToken.None);
            _port.EndTurnOnCut = false;
            SpeechHandle handle = activity.GenerateReply("Tell me a story.");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Here is a long story");
            await _output.WaitForLogAsync(1);

            _ = handle.Interrupt(source: InterruptionSource.UserTurn);
            await _time.WaitForTimersAsync(Start + SpeechHandle.InterruptionTimeout, 1);
            _time.Advance(SpeechHandle.InterruptionTimeout);
            _ = await handle;
            await session.Scheduler.PauseSchedulingAsync();

            Assert.Empty(logs.Of(SpeechTaskFaulted));
            turn.End();
            await turn.Finished;
        }

        // IConversationPort.Recut refuses a turn older than the newest one started. The turn did take its cut, so
        // the refusal is logged as a refused recut, never as a cut refused because the turn was already cut.
        [Fact(Timeout = 30_000)]
        public async Task ARecutRefusedAfterALaterTurnStarted_IsLoggedAsARefusedRecut()
        {
            RecordingLoggerFactory logs = new();
            EngineReplyStream stream = EngineReplyStream.Start(
                _port,
                "First question",
                Task.CompletedTask,
                SpeechHandle.Create(_time, NullLogger.Instance),
                new TurnMetrics(_time, userTurnEndedAt: null, static (_, _, _) => { }),
                logs.CreateLogger("voice"),
                CancellationToken.None);
            _ = await _port.Turn(1).Started;
            Assert.True(stream.Cut(new TurnCut("The first", null)));
            await stream.Completion;
            await using TurnRun later = await _port.StartTurnAsync(
                new ChatMessage(ChatRole.User, "Second question"), origin: null, TestContext.Current.CancellationToken);

            stream.Recut(new TurnCut("The first answer", null));

            Assert.Empty(logs.Of(CutRefused));
            Assert.Equal(0, Assert.Single(logs.Of(RecutRefused)).Field<int>("TurnIndex"));
        }
    }
}

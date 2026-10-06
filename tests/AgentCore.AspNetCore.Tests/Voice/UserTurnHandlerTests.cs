// Portions derived from LiveKit Agents, tests/test_wait_for_idle_cancellation.py and
// tests/test_agent_session.py (test_interim_transcript_interrupts_only_without_local_vad),
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#; driven over a scripted
// engine turn instead of fake VAD/STT/LLM.

using AgentCore.Application.Runtime.Cut;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice.Options;
using AgentCore.AspNetCore.Voice.Session;
using AgentCore.AspNetCore.Voice.Speech;
using AgentCore.AspNetCore.Voice.Turns;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>What a committed (final) and an interim caller prompt do to the current speech and the
    /// away timer, and how <see cref="SpeechScheduler.WaitForIdleAsync"/> waits for the user-turn chain.</summary>
    public sealed class UserTurnHandlerTests : IAsyncDisposable
    {
        private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

        private readonly FakeConversationOutput _output = new();

        private readonly ScriptedVoicePort _port = new();

        private readonly VoiceSession _session;

        private readonly VoiceActivity _activity;

        private readonly UserTurnHandler _handler;

        public UserTurnHandlerTests()
        {
            _session = new VoiceSession(_output, _time, NullLogger.Instance);
            _activity = new VoiceActivity(_session, _port, CancellationToken.None);
            _handler = new UserTurnHandler(_session, _activity);
        }

        public ValueTask DisposeAsync()
        {
            return _output.DisposeAsync();
        }

        // agent_activity.py:2552, 2774-2783: a final transcript interrupts the current speech, as audio activity,
        // and with no report of what was heard the speech cuts its engine turn to the text it forwarded
        // once the wait runs out; the next reply's turn follows it.
        [Fact(Timeout = 30_000)]
        public async Task AFinalTranscript_InterruptsTheCurrentSpeechAndTheNextReplyAnswers()
        {
            SpeechHandle first = _activity.GenerateReply("First question");
            ScriptedVoiceTurn turn1 = _port.Turn(1);
            await turn1.TextAsync("Let me think about that.");
            await _output.WaitForLogAsync(1);

            _handler.OnFinalTranscript("Second question");
            await _time.WaitForTimersAsync(_time.GetUtcNow() + VoiceOptions.DefaultHeardTextWait, 1);
            _time.Advance(VoiceOptions.DefaultHeardTextWait);
            _ = await first;

            Assert.True(first.IsInterrupted);
            Assert.Equal(InterruptionSource.AudioActivity, first.InterruptSource);

            Assert.Equal([(0, new TurnCut("Let me think about that.", null))], _port.Cuts);
            await turn1.Finished;

            Assert.Equal("Second question", await _port.Turn(2).Started);
            await _port.Turn(2).TextAsync("The second answer.");
            _port.Turn(2).End();
            await _session.WaitForIdleAsync(TestContext.Current.CancellationToken);

            Assert.Equal(["Let me think about that.", "The second answer."], _output.Spoken);
        }

        // agent_activity.py:2774-2780: an uninterruptible speech drops the turn instead of answering it.
        [Fact(Timeout = 30_000)]
        public async Task AFinalTranscriptDuringAnUninterruptibleSpeech_DropsTheTurnAndLogsAWarning()
        {
            RecordingLoggerFactory logs = new();
            VoiceSession session = new(_output, _time, logs.CreateLogger("voice"));
            VoiceActivity activity = new(session, _port, CancellationToken.None);
            UserTurnHandler handler = new(session, activity);

            // Held mid-speech, not merely polled to be current: a short "Please hold." can finish
            // speaking (and stop being current) before the check below ever observes it.
            TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _output.BeforeSpeakReturns = (_, cancellationToken) => held.Task.WaitAsync(cancellationToken);
            SpeechHandle notice = session.Say("Please hold.", allowInterruptions: false);
            await _output.WaitForLogAsync(1);

            handler.OnFinalTranscript("Are you still there?");

            await Poll.UntilAsync(() => logs.Of(30).Count == 1);
            CapturedLine line = Assert.Single(logs.Of(30));
            Assert.Equal(LogLevel.Warning, line.Level);
            Assert.False(notice.IsInterrupted);
            Assert.False(_port.Turn(1).Started.IsCompleted);

            _ = held.TrySetResult();
            _ = await notice;
        }

        // test_agent_session.py::test_interim_transcript_interrupts_only_without_local_vad (VAD-present
        // case only, :139): an interim prompt only sets state; it never interrupts.
        [Fact(Timeout = 30_000)]
        public async Task AnInterimTranscript_SetsUserStateSpeakingAndNeverInterruptsTheCurrentSpeech()
        {
            UserStateLog states = new(_session);
            SpeechHandle current = _activity.GenerateReply("Tell me about your day.");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Sure, here goes.");
            await _output.WaitForLogAsync(1);

            _handler.OnInterimTranscript();
            await states.WaitForCountAsync(1);

            Assert.Equal([UserState.Speaking], states.NewStates);
            Assert.False(current.IsInterrupted);
            Assert.Empty(_port.Cuts);
        }

        // test_wait_for_idle_cancellation.py::test_cancelling_wait_for_idle_does_not_cancel_end_of_turn_task
        // (:31): our port has no separate end-of-turn task, so WaitForIdleAsync's only shielded wait is the
        // user-turn task itself (agent_activity.py:2092-2094).
        [Fact(Timeout = 30_000)]
        public async Task CancellingWaitForIdle_DoesNotCancelTheUserTurnTask()
        {
            SpeechScheduler scheduler = new(new Lock(), NullLogger.Instance);
            TaskCompletionSource userTurnGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            scheduler.SetUserTurnTask(userTurnGate.Task);

            using CancellationTokenSource cts = new();
            Task idle = scheduler.WaitForIdleAsync(cts.Token);
            cts.Cancel();

            _ = await Assert.ThrowsAsync<TaskCanceledException>(() => idle);
            Assert.False(userTurnGate.Task.IsCanceled);

            _ = userTurnGate.TrySetResult();
            await userTurnGate.Task;
        }

        // test_wait_for_idle_cancellation.py::test_cancelling_wait_for_idle_does_not_poison_user_turns (:52).
        [Fact(Timeout = 30_000)]
        public async Task CancellingWaitForIdle_DoesNotPoisonTheNextUserTurn()
        {
            TaskCompletionSource staleTurn = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _session.Scheduler.SetUserTurnTask(staleTurn.Task);

            using CancellationTokenSource cts = new();
            Task idle = _session.Scheduler.WaitForIdleAsync(cts.Token);
            cts.Cancel();
            _ = await Assert.ThrowsAsync<TaskCanceledException>(() => idle);
            Assert.False(staleTurn.Task.IsCanceled);

            _ = staleTurn.TrySetResult();
            await Poll.UntilAsync(() => staleTurn.Task.IsCompleted);

            _handler.OnFinalTranscript("Are you there?");
            Assert.Equal("Are you there?", await _port.Turn(1).Started);
            await _port.Turn(1).TextAsync("Yes, still here.");
            _port.Turn(1).End();

            await _session.WaitForIdleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(["Yes, still here."], _output.Spoken);
        }

        // agent_activity.py:2723-2730: the turn waits out the one before it, and that turn's fault is its own.
        [Fact(Timeout = 30_000)]
        public async Task APreviousTurnThatFaults_EndsThisTurnWithThatFaultAndNoReply()
        {
            TaskCompletionSource previous = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _session.Scheduler.SetUserTurnTask(previous.Task);
            _handler.OnFinalTranscript("Hello?");
            Task turn = _session.Scheduler.UserTurnTask!;

            _ = previous.TrySetException(new InvalidOperationException("the earlier turn failed"));

            InvalidOperationException fault = await Assert.ThrowsAsync<InvalidOperationException>(() => turn);
            Assert.Equal("the earlier turn failed", fault.Message);
            Assert.False(_port.Turn(1).Started.IsCompleted);
        }

        // agent_activity.py:2729: a cancelled previous turn is not a fault, so this one still answers.
        [Fact(Timeout = 30_000)]
        public async Task APreviousTurnThatWasCancelled_StillLetsThisTurnAnswer()
        {
            TaskCompletionSource previous = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _session.Scheduler.SetUserTurnTask(previous.Task);
            _handler.OnFinalTranscript("Hello?");
            Task turn = _session.Scheduler.UserTurnTask!;

            _ = previous.TrySetCanceled(TestContext.Current.CancellationToken);
            await turn;

            Assert.Equal("Hello?", await _port.Turn(1).Started);
            _port.Turn(1).End();
            await _session.WaitForIdleAsync(TestContext.Current.CancellationToken);
        }

        // agent_activity.py:2896-2901: a reply whose turn the caller has already moved past is interrupted, but its
        // engine turn still runs, so the caller's words are kept.
        [Fact(Timeout = 30_000)]
        public async Task ANewerFinalTranscript_InterruptsTheOutdatedReplyAndKeepsItsWords()
        {
            TaskCompletionSource firstHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource secondHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _session.Scheduler.SetUserTurnTask(firstHeld.Task);
            _handler.OnFinalTranscript("First question");
            Task first = _session.Scheduler.UserTurnTask!;
            _session.Scheduler.SetUserTurnTask(secondHeld.Task);
            _handler.OnFinalTranscript("Second question");
            Task second = _session.Scheduler.UserTurnTask!;

            _ = firstHeld.TrySetResult();
            await first;
            Assert.Equal("First question", await _port.Turn(1).Started);
            _port.Turn(1).End();
            _session.Scheduler.SetUserTurnTask(null);
            await _session.WaitForIdleAsync(TestContext.Current.CancellationToken);

            Assert.Equal([(0, new TurnCut(string.Empty, null))], _port.Cuts);
            Assert.Empty(_output.Spoken);

            _ = secondHeld.TrySetResult();
            await second;
            Assert.Equal("Second question", await _port.Turn(2).Started);
            await _port.Turn(2).TextAsync("The second answer.");
            _port.Turn(2).End();
            await _session.WaitForIdleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(["The second answer."], _output.Spoken);
        }

        // agent_activity.py:2740-2741 and 2092-2094: a committed turn interrupts the speech still waiting on its
        // tools and waits for it to finish, and wait_for_idle waits for that turn.
        [Fact(Timeout = 30_000)]
        public async Task AFinalTranscriptWhileAToolRuns_InterruptsThatSpeechFirstAndIdleWaitsForTheTurn()
        {
            SpeechHandle looking = _activity.GenerateReply("Where is my order?");
            ScriptedVoiceTurn lookup = _port.Turn(1);
            await lookup.TextAsync("Let me look that up.");
            await lookup.CallAsync("1", "lookup_order");
            await Poll.UntilAsync(() => _session.HasBackgroundSpeeches && _session.Scheduler.CurrentSpeech is null);
            _port.EndTurnOnCut = false;

            _handler.OnFinalTranscript("Never mind.");
            Task turn = _session.Scheduler.UserTurnTask!;
            Task<bool> speechDoneWhenTurnEnded = turn.ContinueWith(
                _ => looking.IsDone,
                TestContext.Current.CancellationToken,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Task idle = _session.WaitForIdleAsync(TestContext.Current.CancellationToken);
            Assert.False(idle.IsCompleted);

            await looking.WaitIfNotInterruptedAsync([turn]);
            Assert.True(looking.IsInterrupted);
            Assert.False(turn.IsCompleted);

            lookup.End();
            await turn;
            Assert.True(await speechDoneWhenTurnEnded, "the turn answered before the speech it interrupted was done.");
            Assert.Equal("Never mind.", await _port.Turn(2).Started);
            _port.Turn(2).End();
            await idle;

            Assert.Equal([(0, new TurnCut("Let me look that up.", null))], _port.Cuts);
        }

        // agent_activity.py:2644-2649 (on_end_of_turn): with scheduling paused, the turn is skipped with a warning.
        [Fact(Timeout = 30_000)]
        public async Task AFinalTranscriptWhileSchedulingIsPaused_StartsNoTurn()
        {
            RecordingLoggerFactory logs = new();
            VoiceSession session = new(_output, _time, logs.CreateLogger("voice"));
            UserTurnHandler handler = new(session, new VoiceActivity(session, _port, CancellationToken.None));
            await session.Scheduler.PauseSchedulingAsync();

            handler.OnFinalTranscript("Hello?");

            CapturedLine line = Assert.Single(logs.Of(31));
            Assert.Equal(LogLevel.Warning, line.Level);
            Assert.Null(session.Scheduler.UserTurnTask);
        }

        // agent_activity.py:2392 and 2433: an interim prompt marks the caller speaking, a final one silent again.
        [Fact(Timeout = 30_000)]
        public async Task AnInterimThenAFinalTranscript_MoveTheCallerToSpeakingAndBack()
        {
            _handler.OnInterimTranscript();
            Assert.Equal(UserState.Speaking, _session.UserState);
            Assert.False(_session.WaitForUserSilenceAsync(TestContext.Current.CancellationToken).IsCompleted);

            _handler.OnFinalTranscript("Hello?");
            Assert.Equal(UserState.Listening, _session.UserState);
            Assert.True(_session.WaitForUserSilenceAsync(TestContext.Current.CancellationToken).IsCompleted);

            _port.Turn(1).End();
            await _session.WaitForIdleAsync(TestContext.Current.CancellationToken);
        }
    }
}

// Portions derived from LiveKit Agents, tests/test_agent_session.py (test_tool_call,
// test_tool_without_a_reply_returns_the_agent_to_listening, test_interruption),
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#; driven over a scripted engine
// turn instead of fake VAD/STT/LLM/TTS; chat-context assertions become assertions on the engine calls.

using System.Text.Json;
using AgentCore.Application.Runtime;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary><see cref="VoiceActivity.GenerateReply"/> and <see cref="PipelineReply"/> over a scripted engine turn.</summary>
    public sealed class PipelineReplyTests : IAsyncDisposable
    {
        private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

        private readonly FakeConversationOutput _output = new();

        private readonly ScriptedVoicePort _port = new();

        private readonly VoiceSession _session;

        private readonly VoiceActivity _activity;

        private readonly AgentStateLog _states;

        public PipelineReplyTests()
        {
            _session = new VoiceSession(_output, _time, NullLogger.Instance);
            _activity = new VoiceActivity(_session, _port, CancellationToken.None);
            _states = new AgentStateLog(_session);
        }

        public ValueTask DisposeAsync()
        {
            return _output.DisposeAsync();
        }

        // test_agent_session.py::test_tool_call
        [Fact(Timeout = 30_000)]
        public async Task AToolCall_SplitsTheReplyIntoTwoStepsWithOneCompletionEach()
        {
            SpeechHandle handle = _activity.GenerateReply("What's the weather in Tokyo?");
            ScriptedVoiceTurn turn = _port.Turn(1);

            await turn.TextAsync("Let me check the weather for you.");
            await turn.CallAsync("1", "get_weather");
            await turn.ResultAsync("1");
            await turn.TextAsync("The weather in Tokyo is sunny today.");
            turn.End();
            _ = await handle;
            await _states.WaitForCountAsync(5);

            Assert.Equal(
                [AgentState.Thinking, AgentState.Speaking, AgentState.Thinking, AgentState.Speaking, AgentState.Listening],
                _states.NewStates);
            Assert.Equal(
                ["speak:Let me check the weather for you.", "complete", "speak:The weather in Tokyo is sunny today.", "complete"],
                _output.Log);
            Assert.Equal(2, handle.NumSteps);
        }

        // test_agent_session.py::test_tool_without_a_reply_returns_the_agent_to_listening
        [Fact(Timeout = 30_000)]
        public async Task AToolWithNoReplyAfterIt_HandsTheTurnBackToListening()
        {
            SpeechHandle handle = _activity.GenerateReply("Look up my order");
            ScriptedVoiceTurn turn = _port.Turn(1);

            await turn.TextAsync("Let me check.");
            await turn.CallAsync("1", "silent_lookup");
            await turn.ResultAsync("1");
            turn.End();
            _ = await handle;
            await _states.WaitForCountAsync(4);

            Assert.Equal(
                [(AgentState.Speaking, AgentState.Thinking), (AgentState.Thinking, AgentState.Listening)],
                _states.Transitions.TakeLast(2));
            Assert.Equal(1, _output.Completions);
        }

        [Fact(Timeout = 30_000)]
        public async Task TheStepAfterATool_WaitsForItsOwnAuthorization()
        {
            // agent_activity.py:4024-4026 schedules the next step as a new speech turn, so a say that took
            // the floor while the tool ran finishes before the next step speaks.
            TaskCompletionSource sayHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _output.BeforeSpeakReturns = (fragment, cancellationToken) =>
                fragment == "One moment." ? sayHeld.Task.WaitAsync(cancellationToken) : Task.CompletedTask;

            SpeechHandle handle = _activity.GenerateReply("What's the weather?");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Checking.");
            await turn.CallAsync("1");

            SpeechHandle say = _session.Say("One moment.");
            await _output.WaitForLogAsync(3);

            await turn.ResultAsync("1");
            await turn.TextAsync("It is sunny.");
            turn.End();
            await Poll.UntilAsync(() => handle.NumSteps == 2);
            Assert.Equal(3, _output.Log.Count);

            _ = sayHeld.TrySetResult();
            _ = await say;
            _ = await handle;

            Assert.Equal(
                ["speak:Checking.", "complete", "speak:One moment.", "complete", "speak:It is sunny.", "complete"],
                _output.Log);
        }

        // test_agent_session.py::test_interruption (resume_false_interruption=False)
        [Fact(Timeout = 30_000)]
        public async Task ABargeInMidText_EndsTheTurnWithTheHeardTextAndTheNextReplyAnswers()
        {
            SpeechHandle handle = _activity.GenerateReply("Tell me a story.");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Here is a long story for you");
            await _output.WaitForLogAsync(1);

            await _activity.InterruptByAudioActivity("Here is a", TimeSpan.FromSeconds(2));

            Assert.Equal([(0, new TurnCut("Here is a", TimeSpan.FromSeconds(2)))], _port.Cuts);
            Assert.Equal(InterruptionSource.AudioActivity, handle.InterruptSource);

            SpeechHandle next = _activity.GenerateReply("Stop!");
            _port.Turn(2).End();
            _ = await next;
            await _states.WaitForCountAsync(5);

            Assert.Equal(
                [AgentState.Thinking, AgentState.Speaking, AgentState.Listening, AgentState.Thinking, AgentState.Listening],
                _states.NewStates);
            Assert.Equal(["Here is a long story for you"], _output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task ABargeInWhileAToolRuns_EndsTheTurnWithTheHeardText()
        {
            SpeechHandle handle = _activity.GenerateReply("Where is my order?");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Let me look that up.");
            await turn.CallAsync("1", "lookup_order");
            await Poll.UntilAsync(() => _session.HasBackgroundSpeeches);

            await _activity.InterruptByAudioActivity("Let me look", TimeSpan.FromMilliseconds(900));
            await turn.Finished;

            Assert.Equal([(0, new TurnCut("Let me look", TimeSpan.FromMilliseconds(900)))], _port.Cuts);
            Assert.True(handle.IsInterrupted);
            Assert.Equal(["Let me look that up."], _output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task ATurnThatEndsWithACallThatNeverAnswered_SpeaksTheTextAfterItOnceAsItsOwnStep()
        {
            // A tool that throws out of the run leaves its call with no result, and the fallback line follows it
            // (owner ruling 2026-09-23: the caller hears the fallback).
            SpeechHandle handle = _activity.GenerateReply("Where is my order?");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Let me check.");
            await turn.CallAsync("1", "lookup_order");
            await turn.TextAsync("Sorry, the order service did not answer.");
            turn.End();
            _ = await handle;

            Assert.Equal(
                ["speak:Let me check.", "complete", "speak:Sorry, the order service did not answer.", "complete"],
                _output.Log);
            Assert.Equal(2, handle.NumSteps);
            Assert.Empty(_port.Cuts);
        }

        [Fact(Timeout = 30_000)]
        public async Task ABargeInBeforeTheTurnEnds_SpeaksNoTextHeldBehindAnUnansweredCall()
        {
            SpeechHandle handle = _activity.GenerateReply("Where is my order?");
            ScriptedVoiceTurn turn = _port.Turn(1);
            await turn.TextAsync("Let me check.");
            await turn.CallAsync("1", "lookup_order");
            await turn.TextAsync("Sorry, the order service did not answer.");
            await Poll.UntilAsync(() => _session.HasBackgroundSpeeches);

            await _activity.InterruptByAudioActivity("Let me check.", TimeSpan.FromMilliseconds(700));
            await turn.Finished;
            _ = await handle;

            Assert.Equal([(0, new TurnCut("Let me check.", TimeSpan.FromMilliseconds(700)))], _port.Cuts);
            Assert.True(handle.IsInterrupted);
            Assert.Equal(["Let me check."], _output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task ABargeInBeforeTheSpeechSaidAnything_CutsNoTurnAndLeavesTheSpeechToSpeak()
        {
            // Turn identity (design section 3): a speech that has put no words out is not what the caller hears,
            // and with no earlier reply heard there is no turn to cut.
            SpeechHandle handle = _activity.GenerateReply("And the other one?");
            ScriptedVoiceTurn turn = _port.Turn(1);
            _ = await turn.Started;
            await Poll.UntilAsync(() => _session.Scheduler.CurrentSpeech == handle);

            await _activity.InterruptByAudioActivity("the earlier answer", TimeSpan.FromMilliseconds(1200));
            await turn.TextAsync("Still here.");
            turn.End();
            _ = await handle;

            Assert.Empty(_port.Cuts);
            Assert.False(handle.IsInterrupted);
            Assert.Equal(["speak:Still here.", "complete"], _output.Log);
        }

        [Fact(Timeout = 30_000)]
        public async Task AUserTurnBeforeTheSpeechSaidAnything_CutsItsTurnWithNothingHeard()
        {
            // Design section 3 row "Cut before output" (W04, G1 = Cut(turn, "", null)): the turn stops and keeps the
            // caller's message, and the next reply's turn follows it.
            SpeechHandle handle = _activity.GenerateReply("First question");
            ScriptedVoiceTurn first = _port.Turn(1);
            _ = await first.Started;
            await Poll.UntilAsync(() => _session.Scheduler.CurrentSpeech == handle);

            _ = handle.Interrupt(source: InterruptionSource.UserTurn);
            _ = await handle;
            await first.Finished;
            SpeechHandle next = _activity.GenerateReply("Second question");

            Assert.Equal("Second question", await _port.Turn(2).Started);
            await _port.Turn(2).TextAsync("The second answer.");
            _port.Turn(2).End();
            _ = await next;

            Assert.Equal([(0, new TurnCut(string.Empty, null))], _port.Cuts);
            Assert.Null(next.Error);
            Assert.Equal(["The second answer."], _output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task AReplyEndingOnAPendingApproval_SpeaksTheNotice()
        {
            // Plan B11: a voice conversation has no surface to answer an approval on.
            SpeechHandle handle = _activity.GenerateReply("Send the email");
            using JsonDocument empty = JsonDocument.Parse("{}");
            _port.LastTurn = new TurnResult(_port.ConversationId, 0, string.Empty, string.Empty, string.Empty, false, null)
            {
                Approvals = [new PendingApproval("req-1", "send_email", empty.RootElement.Clone())],
            };
            _port.Turn(1).End();
            _ = await handle;
            await _output.WaitForLogAsync(2);

            Assert.Equal(["speak:That action needs a human approval, which this conversation can't take.", "complete"], _output.Log);
        }
    }
}

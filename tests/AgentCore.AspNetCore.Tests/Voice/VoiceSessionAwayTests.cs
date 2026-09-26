// Portions derived from LiveKit Agents, tests/test_agent_session.py
// (test_final_transcript_resets_away_timer_when_not_speaking, :1514), commit
// d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#; LiveKit's test mocks
// `_set_user_away_timer` and asserts on the mock. Ours has no mock of that shape, so each case is
// proven behaviourally: whether the away window the test observes is the one that started at the
// state transition, or a fresh one that started at the final transcript.

using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>When the caller counts as away (plan B7), and what a final and an interim transcript each do
    /// to that window (LiveKit's <c>_user_input_transcribed</c>, <c>agent_session.py:2254-2267</c>).</summary>
    public sealed class VoiceSessionAwayTests : IAsyncDisposable
    {
        private static readonly UserAwayOptions Away = new(TimeSpan.FromSeconds(15), "Are you still there?");

        private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

        private readonly FakeConversationOutput _output = new();

        public ValueTask DisposeAsync()
        {
            return _output.DisposeAsync();
        }

        [Fact(Timeout = 30_000)]
        public async Task BothSidesListeningForTheFullTimeout_MarksTheCallerAwayAndSpeaksThePrompt()
        {
            VoiceSession session = new(_output, _time, NullLogger.Instance, Away);
            UserStateLog states = new(session);

            session.SetAgentState(AgentState.Thinking);
            session.SetAgentState(AgentState.Listening);

            _time.Advance(TimeSpan.FromSeconds(15) - TimeSpan.FromTicks(1));
            Assert.Equal(UserState.Listening, session.UserState);

            _time.Advance(TimeSpan.FromTicks(1));
            await _output.WaitForLogAsync(2);

            Assert.Equal([UserState.Away], states.NewStates);
            Assert.Equal(["Are you still there?"], _output.Spoken);
        }

        // agent_session.py:2025-2027 (_RunningTasks guard): a tool call still open holds the window open.
        [Fact(Timeout = 30_000)]
        public void AToolCallStillOpen_NeverArmsTheAwayTimer()
        {
            VoiceSession session = new(_output, _time, NullLogger.Instance, Away);
            SpeechHandle background = SpeechHandle.Create(_time, NullLogger.Instance);
            session.Scheduler.AddBackgroundSpeech(background);

            session.SetAgentState(AgentState.Thinking);
            session.SetAgentState(AgentState.Listening);
            _time.Advance(TimeSpan.FromSeconds(30));

            Assert.Equal(UserState.Listening, session.UserState);
            Assert.Empty(_output.Spoken);
        }

        // test_final_transcript_resets_away_timer_when_not_speaking: final transcript, both sides listening.
        [Fact(Timeout = 30_000)]
        public async Task AFinalTranscriptWhileBothSidesListen_RestartsTheAwayWindow()
        {
            VoiceSession session = new(_output, _time, NullLogger.Instance, Away);
            session.SetAgentState(AgentState.Thinking);
            session.SetAgentState(AgentState.Listening);

            _time.Advance(TimeSpan.FromSeconds(10));
            session.RefreshUserAwayOnFinalTranscript();
            _time.Advance(TimeSpan.FromSeconds(10));

            // 20 s since the state transition, but only 10 s since the reset: still listening.
            Assert.Equal(UserState.Listening, session.UserState);

            _time.Advance(TimeSpan.FromSeconds(5));
            await Poll.UntilAsync(() => session.UserState == UserState.Away);
        }

        // test_final_transcript_resets_away_timer_when_not_speaking (is_final=False half): an interim
        // transcript never calls the away-timer reset LiveKit's mock asserts on. Ours has no such reset
        // call for an interim either — OnInterimTranscript only ever moves the caller to "speaking",
        // which cancels a pending countdown the same way any non-listening transition does (B8).
        [Fact]
        public void AnInterimTranscript_CancelsAPendingAwayCountdownInsteadOfResettingIt()
        {
            VoiceSession session = new(_output, _time, NullLogger.Instance, Away);
            VoiceActivity activity = new(session, new ScriptedVoicePort(), CancellationToken.None);
            UserTurnHandler handler = new(session, activity);

            session.SetAgentState(AgentState.Thinking);
            session.SetAgentState(AgentState.Listening);

            _time.Advance(TimeSpan.FromSeconds(10));
            handler.OnInterimTranscript();
            _time.Advance(TimeSpan.FromSeconds(10));

            // 20 s since the state transition, but the interim moved the caller to "speaking" at the
            // 10 s mark, cancelling the countdown; nothing re-arms it until a final transcript returns
            // the caller to "listening".
            Assert.Equal(UserState.Speaking, session.UserState);
            Assert.Empty(_output.Spoken);
        }

        // test_final_transcript_resets_away_timer_when_not_speaking: final transcript while speaking is a no-op.
        [Fact]
        public void AFinalTranscriptWhileTheCallerIsSpeaking_NeverArmsTheAwayTimer()
        {
            VoiceSession session = new(_output, _time, NullLogger.Instance, Away);
            session.SetUserState(UserState.Speaking);

            session.RefreshUserAwayOnFinalTranscript();
            _time.Advance(TimeSpan.FromSeconds(30));

            Assert.Equal(UserState.Speaking, session.UserState);
        }

        // agent_session.py:2166-2169 (_update_agent_state): only "listening" on both sides arms the window.
        [Fact]
        public void TheAgentBackToListeningWhileTheCallerSpeaks_NeverArmsTheAwayTimer()
        {
            VoiceSession session = new(_output, _time, NullLogger.Instance, Away);
            session.SetUserState(UserState.Speaking);

            session.SetAgentState(AgentState.Thinking);
            session.SetAgentState(AgentState.Listening);
            _time.Advance(TimeSpan.FromSeconds(30));

            Assert.Equal(UserState.Speaking, session.UserState);
            Assert.Empty(_output.Spoken);
        }

        // agent_session.py:2166-2169: any other agent state cancels the countdown.
        [Fact]
        public void TheAgentStartingToSpeak_CancelsThePendingAwayCountdown()
        {
            VoiceSession session = new(_output, _time, NullLogger.Instance, Away);
            session.Start();

            session.SetAgentState(AgentState.Speaking);
            _time.Advance(TimeSpan.FromSeconds(30));

            Assert.Equal(UserState.Listening, session.UserState);
            Assert.Empty(_output.Spoken);
        }

        // agent_session.py:2223-2226 (_update_user_state): the caller falling silent while the agent speaks arms nothing.
        [Fact]
        public void TheCallerFallingSilentWhileTheAgentSpeaks_NeverArmsTheAwayTimer()
        {
            VoiceSession session = new(_output, _time, NullLogger.Instance, Away);
            session.SetAgentState(AgentState.Speaking);

            session.SetUserState(UserState.Speaking);
            session.SetUserState(UserState.Listening);
            _time.Advance(TimeSpan.FromSeconds(30));

            Assert.Equal(UserState.Listening, session.UserState);
            Assert.Empty(_output.Spoken);
        }

        // agent_session.py:2263-2265 (_user_input_transcribed): a final transcript refreshes the window only while
        // the agent listens too.
        [Fact]
        public void AFinalTranscriptWhileTheAgentThinks_NeverArmsTheAwayTimer()
        {
            VoiceSession session = new(_output, _time, NullLogger.Instance, Away);
            session.SetAgentState(AgentState.Thinking);

            session.RefreshUserAwayOnFinalTranscript();
            _time.Advance(TimeSpan.FromSeconds(30));

            Assert.Equal(UserState.Listening, session.UserState);
            Assert.Empty(_output.Spoken);
        }

        // agent_session.py:1159-1160: the session starts with the agent listening, which arms the window only
        // while the caller listens too.
        [Fact]
        public void StartingWhileTheCallerSpeaks_NeverArmsTheAwayTimer()
        {
            VoiceSession session = new(_output, _time, NullLogger.Instance, Away);
            session.SetUserState(UserState.Speaking);

            session.Start();
            _time.Advance(TimeSpan.FromSeconds(30));

            Assert.Equal(UserState.Speaking, session.UserState);
            Assert.Empty(_output.Spoken);
        }

        // agent_session.py:2195-2196: setting the state the caller is already in changes nothing, not even the window.
        [Fact(Timeout = 30_000)]
        public async Task SettingTheUserStateItAlreadyHas_RaisesNothingAndKeepsTheWindow()
        {
            VoiceSession session = new(_output, _time, NullLogger.Instance, Away);
            UserStateLog states = new(session);
            session.Start();
            _time.Advance(TimeSpan.FromSeconds(10));

            session.SetUserState(UserState.Listening);
            Assert.Empty(states.NewStates);

            _time.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal([UserState.Away], states.NewStates);
            await _output.WaitForLogAsync(2);
        }
    }
}

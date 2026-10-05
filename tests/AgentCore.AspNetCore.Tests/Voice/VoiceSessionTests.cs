using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice.Session;
using AgentCore.AspNetCore.Voice.Speech;
using AgentCore.AspNetCore.Voice.Speech.Replies;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary><see cref="VoiceSession.Say"/>, <see cref="VoiceSession.WaitForIdleAsync"/> and the agent
    /// state transitions LiveKit's <c>_update_agent_state</c>/<c>_on_pipeline_reply_done</c> drive around
    /// a <c>say</c>.</summary>
    public sealed class VoiceSessionTests
    {
        private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

        [Fact(Timeout = 30_000)]
        public async Task Say_SpeaksTheTextAndClosesTheReply()
        {
            FakeConversationOutput output = new();
            VoiceSession session = new(output, _time, NullLogger.Instance);

            SpeechHandle handle = session.Say("hello there");
            _ = await handle;

            Assert.Equal(["hello there"], output.Spoken);
            Assert.Equal(1, output.Completions);
        }

        // test_agent_session.py::test_tool_call (states half): agent state follows _update_agent_state.
        [Fact(Timeout = 30_000)]
        public async Task Say_MovesAgentStateThroughThinkingSpeakingListening()
        {
            FakeConversationOutput output = new();
            VoiceSession session = new(output, _time, NullLogger.Instance);
            List<AgentState> transitions = [];
            session.AgentStateChanged += change => transitions.Add(change.NewState);

            // A filler or a notice plays while a pipeline reply's tool round has the agent "thinking";
            // simulate that starting point directly.
            session.SetAgentState(AgentState.Thinking);

            SpeechHandle handle = session.Say("one moment");
            _ = await handle;

            Assert.Equal([AgentState.Thinking, AgentState.Speaking, AgentState.Listening], transitions);
        }

        [Fact(Timeout = 30_000)]
        public async Task AnInterruptedSay_ForwardsNothingFurther()
        {
            FakeConversationOutput output = new();
            VoiceSession session = new(output, _time, NullLogger.Instance);
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);

            // Never authorized: the only way SayReply's authorization wait can resolve is the
            // interruption itself, so this is deterministic regardless of thread-pool timing.
            Task run = SayReply.RunAsync(handle, session, output, Fragment("hello"), handle.TaskCancellationToken);
            _ = handle.Interrupt();
            await run;

            Assert.Empty(output.Spoken);
            Assert.Equal(0, output.Completions);
        }

        [Fact(Timeout = 30_000)]
        public async Task WaitForIdle_ThrowsOnceTheSessionIsClosed()
        {
            FakeConversationOutput output = new();
            VoiceSession session = new(output, _time, NullLogger.Instance);

            session.MarkClosed();

            _ = await Assert.ThrowsAsync<VoiceSessionClosedException>(
                () => session.WaitForIdleAsync(TestContext.Current.CancellationToken));
        }

        // agent_activity.py _tts_task_impl: an interruptible say also waits for the user's silence.
        [Fact(Timeout = 30_000)]
        public async Task AnInterruptibleSay_WaitsForTheUsersSilence()
        {
            FakeConversationOutput output = new();
            VoiceSession session = new(output, _time, NullLogger.Instance);
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance, allowInterruptions: true);
            Assert.True(handle.TryAuthorizeGeneration());
            session.ClearUserSilence();

            Task run = SayReply.RunAsync(handle, session, output, Fragment("hello"), handle.TaskCancellationToken);
            Assert.Empty(output.Spoken);

            session.MarkUserSilent();
            await run;
            Assert.Equal(["hello"], output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task ASayThatDisallowsInterruptions_SpeaksWhileTheUserIsSpeaking()
        {
            FakeConversationOutput output = new();
            VoiceSession session = new(output, _time, NullLogger.Instance);
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance, allowInterruptions: false);
            Assert.True(handle.TryAuthorizeGeneration());
            session.ClearUserSilence();

            await SayReply.RunAsync(handle, session, output, Fragment("hello"), handle.TaskCancellationToken);

            Assert.Equal(["hello"], output.Spoken);
        }

        // agent_activity.py _tts_task_impl: "thinking" when a speech still waits on its tools, else "listening".
        [Fact(Timeout = 30_000)]
        public async Task ASayThatEndsWhileASpeechWaitsOnItsTools_LeavesTheAgentThinking()
        {
            FakeConversationOutput output = new();
            VoiceSession session = new(output, _time, NullLogger.Instance);
            session.Scheduler.AddBackgroundSpeech(SpeechHandle.Create(_time, NullLogger.Instance));
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);
            Assert.True(handle.TryAuthorizeGeneration());

            await SayReply.RunAsync(handle, session, output, Fragment("one moment"), handle.TaskCancellationToken);

            Assert.Equal(AgentState.Thinking, session.AgentState);
        }

        // agent_activity.py _tts_task_impl: the state changes back only if the agent is still "speaking".
        [Fact(Timeout = 30_000)]
        public async Task ASayThatSpokeNothing_LeavesTheAgentStateAlone()
        {
            FakeConversationOutput output = new();
            VoiceSession session = new(output, _time, NullLogger.Instance);
            session.SetAgentState(AgentState.Thinking);
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);
            Assert.True(handle.TryAuthorizeGeneration());

            await SayReply.RunAsync(handle, session, output, NoFragments(), handle.TaskCancellationToken);

            Assert.Equal(AgentState.Thinking, session.AgentState);
        }

        // agent_activity.py:3089-3095 (_on_pipeline_reply_done): a say that ends without speaking hands the floor back.
        [Fact(Timeout = 30_000)]
        public async Task ASayInterruptedBeforeItSpoke_HandsTheFloorBackToListening()
        {
            VoiceSession session = new(new FakeConversationOutput(), _time, NullLogger.Instance);
            session.SetAgentState(AgentState.Thinking);
            session.ClearUserSilence();

            SpeechHandle handle = session.Say("one moment");
            Task afterDone = AfterDoneCallbacks(handle);
            await Poll.UntilAsync(() => session.Scheduler.CurrentSpeech == handle);
            _ = handle.Interrupt();
            await afterDone;

            Assert.Equal(AgentState.Listening, session.AgentState);
        }

        // agent_activity.py:3086-3090 (_no_pending_speech): the floor stays taken while another speech is queued.
        [Fact(Timeout = 30_000)]
        public async Task ASayEndingWithAnotherQueued_KeepsTheAgentState()
        {
            FakeConversationOutput output = new();
            VoiceSession session = new(output, _time, NullLogger.Instance);
            session.SetAgentState(AgentState.Thinking);
            session.ClearUserSilence();

            SpeechHandle first = session.Say("one moment");
            SpeechHandle second = session.Say("still here");
            Task afterDone = AfterDoneCallbacks(first);
            await Poll.UntilAsync(() => session.Scheduler.CurrentSpeech == first);
            _ = first.Interrupt();
            await afterDone;

            Assert.Equal(AgentState.Thinking, session.AgentState);

            session.MarkUserSilent();
            _ = await second;
            Assert.Equal(["still here"], output.Spoken);
        }

        // agent_activity.py:3091-3095: a speech still waiting on its tools keeps the agent thinking (#6904).
        [Fact(Timeout = 30_000)]
        public async Task ASayEndingWhileAToolRuns_LeavesTheAgentThinking()
        {
            VoiceSession session = new(new FakeConversationOutput(), _time, NullLogger.Instance);
            session.Scheduler.AddBackgroundSpeech(SpeechHandle.Create(_time, NullLogger.Instance));
            session.SetAgentState(AgentState.Thinking);
            session.ClearUserSilence();

            SpeechHandle handle = session.Say("one moment");
            Task afterDone = AfterDoneCallbacks(handle);
            await Poll.UntilAsync(() => session.Scheduler.CurrentSpeech == handle);
            _ = handle.Interrupt();
            await afterDone;

            Assert.Equal(AgentState.Thinking, session.AgentState);
        }

        private static async IAsyncEnumerable<string> NoFragments()
        {
            await Task.CompletedTask;
            yield break;
        }

        /// <summary>Completes once every done callback registered on <paramref name="handle"/> before this one has run:
        /// they run in registration order, on one thread-pool item.</summary>
        private static Task AfterDoneCallbacks(SpeechHandle handle)
        {
            TaskCompletionSource ran = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handle.AddDoneCallback(_ => ran.TrySetResult());
            return ran.Task;
        }

        private static async IAsyncEnumerable<string> Fragment(string text)
        {
            yield return text;
        }
    }
}

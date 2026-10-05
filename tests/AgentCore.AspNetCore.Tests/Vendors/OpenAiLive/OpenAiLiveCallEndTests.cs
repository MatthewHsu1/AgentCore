using System.Text.Json.Nodes;
using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Call;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Xunit;
using static AgentCore.AspNetCore.Tests.Vendors.OpenAiLive.LiveSent;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>When a call whose engine ended hangs up, over a recorded event log.</summary>
    public sealed class OpenAiLiveCallEndTests
    {
        private static readonly DateTimeOffset Noon = RunningLiveCall.Noon;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // The ack marks the start of speech, so the hang-up waits until GPT-Live's speech has been quiet for
        // EndQuietWait. A caller's words do not hold the line up. Both the agent's last line and the caller's goodbye
        // inside the quiet wait still reach the hooks after ConversationEnded: a spoken line is a record of the call.
        // The clock stays short of CallAsks.OlderAskWait, so an end run from inside the ask's own
        // turn would hang this test until its timeout.
        [Fact(Timeout = 30_000)]
        public async Task AnEngineEndHangsUpOnceTheAgentWentQuietAfterTheAck()
        {
            RecordingHook hook = new();
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), [hook], EndsAfterTheFirstAskYaml);
            IReadOnlyList<string> log = LiveLog.Inbound("p1-a");
            int delegation = LiveLog.IndexOf(log, OpenAiLiveEvents.DelegationCreated);
            int callerAgain = delegation + log.Skip(delegation).TakeWhile(json => TypeOf(json) != OpenAiLiveEvents.InputTranscriptDelta).Count();
            List<string> answerSpoken = [.. log.Take(callerAgain).Skip(delegation + 1).Where(json => TypeOf(json) == OpenAiLiveEvents.OutputTranscriptDelta)];
            List<string> callerSaysBye = [.. log.Skip(callerAgain).TakeWhile(json => TypeOf(json) != OpenAiLiveEvents.OutputTranscriptDelta)];

            running.Sideband.Push(log.Take(delegation + 1));
            JsonObject commentary = await running.Sideband.WaitForSentAsync(IsCommentary);
            running.Sideband.Ack(commentary);
            await PushAndDrainAsync(running, answerSpoken.Take(1));
            running.Time.Advance(TimeSpan.FromSeconds(1.2));
            await PushAndDrainAsync(running, answerSpoken.Skip(1).Take(1));
            running.Time.Advance(TimeSpan.FromSeconds(1.2));
            await PushAndDrainAsync(running, answerSpoken.Skip(2));
            bool hungUpWhileSpeaking = running.HungUp.Task.IsCompleted;
            await PushAndDrainAsync(running, callerSaysBye);
            running.Time.Advance(OpenAiLiveCall.EndQuietWait);
            _ = await running.HungUp.Task;
            await running.Loop;
            _ = await hook.WaitForAsync<ConversationUnloaded>();

            Assert.False(hungUpWhileSpeaking);
            Assert.True(running.Call.HasEnded);
            Assert.Equal(
                [
                    (Speaker.Agent, "Sure. Checking that now. Okay. That order shipped on September 24 with UPS. It should arrive on Tuesday, September 29."),
                    (Speaker.Caller, "Okay. Thanks. That's all"),
                ],
                hook.Of<LineSpoken>().TakeLast(2).Select(line => (line.Speaker, line.Text)));
        }

        // A second answer after the engine end (the fallback of a refused ask) starts the quiet wait again; the first
        // answer's wait must not hang up in the middle of it.
        [Fact(Timeout = 30_000)]
        public async Task ASecondEndedAnswerReplacesTheQuietWaitOfTheFirst()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), [], EndsAfterTheFirstAskYaml);
            IReadOnlyList<string> log = LiveLog.Inbound("p1-a");
            JsonObject heard = new() { ["type"] = OpenAiLiveEvents.InputTranscriptDelta, ["delta"] = " One more thing.", ["start_ms"] = 20000, ["end_ms"] = 21000 };
            JsonObject asked = new() { ["type"] = OpenAiLiveEvents.DelegationCreated, ["delegation"] = new JsonObject { ["id"] = "item_second" } };

            running.Sideband.Push(log.Take(LiveLog.IndexOf(log, OpenAiLiveEvents.DelegationCreated) + 1));
            running.Sideband.Ack(await running.Sideband.WaitForSentAsync(IsCommentary));
            await running.Sideband.WaitForDrainAsync();
            running.Time.Advance(TimeSpan.FromSeconds(1));
            running.Sideband.Push([heard.ToJsonString(), asked.ToJsonString()]);
            running.Sideband.Ack(await running.Sideband.WaitForSentAsync(sent => IsCommentary(sent) && (string?)sent["delegation_id"] == "item_second"));
            await running.Sideband.WaitForDrainAsync();
            running.Time.Advance(TimeSpan.FromSeconds(1));
            running.Sideband.Push([log[LiveLog.IndexOf(log, "session.usage.updated")]]);
            Task drained = running.Sideband.WaitForDrainAsync();
            Task first = await Task.WhenAny(drained, running.HungUp.Task);
            running.Time.Advance(OpenAiLiveCall.EndQuietWait);
            _ = await running.HungUp.Task;

            Assert.Same(drained, first);
        }

        // An append GPT-Live never acks (an error, a lost frame) still ends the call.
        [Fact(Timeout = 30_000)]
        public async Task AnEngineEndWithNoAckHangsUpAfterTheAckWait()
        {
            using EventObservedLoggerProvider ackLate = new("AckLate");
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(
                new StallOnCueChatClient(), [], EndsAfterTheFirstAskYaml, ackLate.CreateLogger("test"));
            IReadOnlyList<string> log = LiveLog.Inbound("p1-a");

            running.Sideband.Push(log.Take(LiveLog.IndexOf(log, OpenAiLiveEvents.DelegationCreated) + 1));
            _ = await running.Sideband.WaitForSentAsync(IsCommentary);
            await running.Time.WaitForTimersAsync(Noon + OpenAiLiveCall.AckWait, 1);
            bool hungUpEarly = running.HungUp.Task.IsCompleted;
            running.Time.Advance(OpenAiLiveCall.AckWait);
            _ = await running.HungUp.Task;
            await running.Loop;
            await ackLate.Observed;

            Assert.False(hungUpEarly);
            Assert.True(running.Call.HasEnded);
        }

        // Only the ack of the last piece starts the quiet wait.
        [Fact(Timeout = 30_000)]
        public async Task AMultiPieceAnswerEndsOnlyAfterItsLastPieceIsAcked()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), [], EndsAfterTheFirstAskYaml);
            string words = string.Join(' ', Enumerable.Range(1, 120).Select(n => $"Sentence number {n:D3} is here."));
            JsonObject heard = new() { ["type"] = OpenAiLiveEvents.InputTranscriptDelta, ["delta"] = " " + words, ["start_ms"] = 1000, ["end_ms"] = 2000 };
            JsonObject asked = new() { ["type"] = OpenAiLiveEvents.DelegationCreated, ["delegation"] = new JsonObject { ["id"] = "item_long" } };

            running.Sideband.Push([heard.ToJsonString(), asked.ToJsonString()]);
            _ = await running.Sideband.WaitForSentAsync(sent => IsCommentary(sent) && ((string?)sent["content"])!.EndsWith("Sentence number 120 is here.", StringComparison.Ordinal));
            List<JsonObject> pieces = [.. running.Sideband.Sent.Where(IsCommentary)];
            running.Sideband.Ack(pieces[0]);
            await running.Sideband.WaitForDrainAsync();
            running.Time.Advance(OpenAiLiveCall.EndQuietWait);
            bool hungUpOnTheFirstAck = running.HungUp.Task.IsCompleted;
            running.Sideband.Ack(pieces[^1]);
            await running.Sideband.WaitForDrainAsync();
            running.Time.Advance(OpenAiLiveCall.EndQuietWait);
            _ = await running.HungUp.Task;

            Assert.True(pieces.Count > 1);
            Assert.False(hungUpOnTheFirstAck);
        }

        // A caller who hangs up before the ack is not hung up again, and the call still finishes.
        [Fact(Timeout = 30_000)]
        public async Task ACallerHangUpBeforeTheAckFinishesTheCallWithNoHangUp()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), [], EndsAfterTheFirstAskYaml);
            IReadOnlyList<string> log = LiveLog.Inbound("p1-a");
            int delegation = LiveLog.IndexOf(log, OpenAiLiveEvents.DelegationCreated);

            running.Sideband.Push(log.Take(delegation + 1));
            _ = await running.Sideband.WaitForSentAsync(IsCommentary);
            running.Sideband.Push([log[LiveLog.IndexOf(log, OpenAiLiveEvents.Closed)]]);
            await running.Loop;

            Assert.False(running.HungUp.Task.IsCompleted);
            Assert.True(running.Call.HasEnded);
        }

        // With no commentary to wait for, the hang-up is at once. The clock never moves, so no wait on a
        // timer (CallAsks.OlderAskWait included) can be on this path.
        [Fact(Timeout = 30_000)]
        public async Task AnEngineEndWithNoCommentaryPendingHangsUpAtOnce()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), []);
            IReadOnlyList<string> log = LiveLog.Inbound("p1-a");
            _ = running.Call.Session.EndConversation(ConversationEndReason.AgentCompleted);

            running.Sideband.Push([log[LiveLog.IndexOf(log, OpenAiLiveEvents.DelegationCreated)]]);
            _ = await running.HungUp.Task;
            await running.Loop;

            Assert.True(running.Call.HasEnded);
            Assert.Equal([OpenAiLiveEvents.ThinkingAppend], AfterTheGreeting(running).Select(sent => (string?)sent["type"]));
        }

        // An end from outside every delegation, such as an operator's end while the front
        // voice chats, hangs up too, with the goodbye safety: once GPT-Live's speech has been quiet for EndQuietWait.
        [Fact(Timeout = 30_000)]
        public async Task AnEngineEndOutsideADelegationHangsUpOnceTheAgentWentQuiet()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), []);
            IReadOnlyList<string> log = LiveLog.Inbound("p1-a");
            string agentSpeaks = log.First(json => TypeOf(json) == OpenAiLiveEvents.OutputTranscriptDelta);

            _ = running.Call.Session.EndConversation(ConversationEndReason.AgentCompleted);
            await running.Time.WaitForTimersAsync(Noon + OpenAiLiveCall.EndQuietWait, 1).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            running.Time.Advance(TimeSpan.FromSeconds(1.5));
            await PushAndDrainAsync(running, [agentSpeaks]);
            running.Time.Advance(TimeSpan.FromSeconds(1));
            bool hungUpWhileSpeaking = running.HungUp.Task.IsCompleted;
            running.Time.Advance(TimeSpan.FromSeconds(1));
            _ = await running.HungUp.Task;
            await running.Loop;

            Assert.False(hungUpWhileSpeaking);
            Assert.True(running.Call.HasEnded);
            Assert.Empty(AfterTheGreeting(running));
        }

        // After such an end, a delegation's answer (the fallback of the refused ask) still follows the same rule: only the ack
        // of its last piece starts the quiet wait, so the wait the end armed must not hang up before it.
        [Fact(Timeout = 30_000)]
        public async Task AnAnswerAfterAnEndOutsideADelegationHangsUpOnlyAfterItsAck()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), []);
            IReadOnlyList<string> log = LiveLog.Inbound("p1-a");
            _ = running.Call.Session.EndConversation(ConversationEndReason.AgentCompleted);
            await running.Time.WaitForTimersAsync(Noon + OpenAiLiveCall.EndQuietWait, 1).WaitAsync(TimeSpan.FromSeconds(5), Ct);

            running.Sideband.Push(log.Take(LiveLog.IndexOf(log, OpenAiLiveEvents.DelegationCreated) + 1));
            JsonObject commentary = await running.Sideband.WaitForSentAsync(IsCommentary);
            running.Time.Advance(OpenAiLiveCall.EndQuietWait);
            running.Sideband.Push([log[LiveLog.IndexOf(log, "session.usage.updated")]]);
            Task drained = running.Sideband.WaitForDrainAsync();
            Assert.Same(drained, await Task.WhenAny(drained, running.HungUp.Task));
            running.Sideband.Ack(commentary);
            await running.Sideband.WaitForDrainAsync();
            running.Time.Advance(OpenAiLiveCall.EndQuietWait);
            _ = await running.HungUp.Task;
        }

        // The loop is held in its greeting send while the caller's words arrive and the end comes due, so its next
        // receive and the end are both complete when it looks: the words are still a line of the call.
        [Fact(Timeout = 30_000)]
        public async Task WordsReceivedTogetherWithAnEndStillReachTheHooks()
        {
            RecordingHook hook = new();
            TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeSideband sideband = new() { SendsHeldUntil = held.Task };
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), [hook], sideband: sideband);
            JsonObject heard = new() { ["type"] = OpenAiLiveEvents.InputTranscriptDelta, ["delta"] = " Bye now.", ["start_ms"] = 1000, ["end_ms"] = 2000 };

            _ = await sideband.WaitForSentAsync(_ => true);
            sideband.Push([heard.ToJsonString()]);
            _ = running.Call.Session.EndConversation(ConversationEndReason.AgentCompleted);
            await running.Time.WaitForTimersAsync(Noon + OpenAiLiveCall.EndQuietWait, 1).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            running.Time.Advance(OpenAiLiveCall.EndQuietWait);
            held.SetResult();
            _ = await running.HungUp.Task;
            await running.Loop;
            _ = await hook.WaitForAsync<ConversationUnloaded>();

            Assert.Contains((Speaker.Caller, "Bye now."), hook.Of<LineSpoken>().Select(line => (line.Speaker, line.Text)));
        }
    }
}

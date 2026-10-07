using System.Text.Json.Nodes;
using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Xunit;
using static AgentCore.AspNetCore.Tests.Vendors.OpenAiLive.LiveSent;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>Calls over recorded event logs, with a real call core and a fake model.</summary>
    public sealed class OpenAiLiveCallTests
    {
        private static readonly DateTimeOffset Noon = RunningLiveCall.Noon;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact(Timeout = 30_000)]
        public async Task TheP1CallSpeaksOurAnswerUnderItsDelegationAndEndsOnClose()
        {
            RecordingHook hook = new();
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), [hook]);
            IReadOnlyList<string> log = LiveLog.Inbound("p1-a");
            int delegation = LiveLog.IndexOf(log, OpenAiLiveEvents.DelegationCreated);

            running.Sideband.Push(log.Take(delegation + 1));
            JsonObject commentary = await running.Sideband.WaitForSentAsync(IsCommentary);
            running.Sideband.Push(log.Skip(delegation + 1));
            await running.Loop;
            ConversationEnded ended = await hook.WaitForAsync<ConversationEnded>();

            Assert.Equal("item_ESevHlqkAJkwt1wThn5gW", (string?)commentary["delegation_id"]);
            Assert.Equal("reply to Can you check the status of my order? The order number is A four four seven one", (string?)commentary["content"]);
            Assert.Equal(
                [
                    (Speaker.Caller, "Hi there"),
                    (Speaker.Agent, "Hey! Thanks for calling Sole Fitness. How can I help?"),
                    (Speaker.Caller, "Can you check the status of my order? The order number is A four four seven one"),
                    (Speaker.Agent, "Sure. Checking that now. Okay. That order shipped on September 24 with UPS. It should arrive on Tuesday, September 29."),
                    (Speaker.Caller, "Okay. Thanks. That's all"),
                    (Speaker.Agent, "You're welcome. Take care!"),
                ],
                hook.Of<LineSpoken>().Select(line => (line.Speaker, line.Text)));
            LineSpoken first = hook.Of<LineSpoken>()[0];
            Assert.Equal((Noon.AddMilliseconds(1200), Noon.AddMilliseconds(1800)), (first.StartedAt, first.EndedAt));
            Assert.Equal((ConversationEndReason.CallerHungUp, "close_requested"), (ended.Reason, ended.Call!.Cause));
            Assert.True(running.Sideband.Disposed);
        }

        // Only the newer delegation is answered.
        [Fact(Timeout = 30_000)]
        public async Task ACorrectionLeavesTheOlderDelegationUnansweredAndAnswersTheNewer()
        {
            StallOnCueChatClient model = new() { HoldFirst = true };
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(model, []);
            IReadOnlyList<string> log = LiveLog.Inbound("p3-drop-0");
            int first = LiveLog.IndexOf(log, OpenAiLiveEvents.DelegationCreated);
            int second = LiveLog.IndexOf(log, OpenAiLiveEvents.DelegationCreated, nth: 2);

            running.Sideband.Push(log.Take(first + 1));
            await model.FirstEntered.Task;
            running.Sideband.Push(log.Take(second + 1).Skip(first + 1));
            JsonObject commentary = await running.Sideband.WaitForSentAsync(IsCommentary);
            running.Sideband.Push(log.Skip(second + 1));
            await running.Loop;

            Assert.Equal("item_ESexZMJMkWUoowj5D7061", (string?)commentary["delegation_id"]);
            // The newer ask is a resend that carries the older ask's words before its own.
            Assert.Equal("reply to What is the maximum speed of the Sole F63 treadmill? Oh, wait, sorry. I meant the F80, not the F63", (string?)commentary["content"]);
            Assert.DoesNotContain(running.Sideband.Sent, sent => (string?)sent["delegation_id"] == "item_ESexRCCGscju2z3LSZNCl" && IsCommentary(sent));
        }

        // Nothing goes out once the call ended: the socket may already be gone.
        [Fact(Timeout = 30_000)]
        public async Task AHangUpDuringAnAskSendsNoCommentary()
        {
            RecordingHook hook = new();
            StallOnCueChatClient model = new() { HoldFirst = true };
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(model, [hook]);
            IReadOnlyList<string> log = LiveLog.Inbound("p2-strict-0");
            int delegation = LiveLog.IndexOf(log, OpenAiLiveEvents.DelegationCreated);

            running.Sideband.Push(log.Take(delegation + 1));
            await model.FirstEntered.Task;
            running.Sideband.Push([log[LiveLog.IndexOf(log, OpenAiLiveEvents.Closed)]]);
            await running.Loop;
            ConversationEnded ended = await hook.WaitForAsync<ConversationEnded>();

            Assert.DoesNotContain(running.Sideband.Sent, IsCommentary);
            Assert.Equal("close_requested", ended.Call!.Cause);
        }

        // Tool progress is silent context for the open delegation.
        [Fact(Timeout = 30_000)]
        public async Task EachToolCallGoesOutAsThinkingBeforeTheAnswer()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new GatedToolCallingChatClient(), []);
            IReadOnlyList<string> log = LiveLog.Inbound("p2-strict-0");
            int delegation = LiveLog.IndexOf(log, OpenAiLiveEvents.DelegationCreated);

            running.Sideband.Push(log.Take(delegation + 1));
            _ = await running.Sideband.WaitForSentAsync(IsCommentary);
            running.Sideband.Push(log.Skip(delegation + 1));
            await running.Loop;

            Assert.Equal(
                [OpenAiLiveEvents.ThinkingAppend, OpenAiLiveEvents.CommentaryAppend],
                AfterTheGreeting(running).Select(sent => (string?)sent["type"]));
            Assert.Equal("The backend is running the tool price_lookup. No answer yet.", (string?)AfterTheGreeting(running).First()["content"]);
        }

        [Fact(Timeout = 30_000)]
        public async Task ASidebandThatClosesWithNoSessionClosedEndsTheCallAsFaulted()
        {
            RecordingHook hook = new();
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), [hook]);

            running.Sideband.Push(LiveLog.Inbound("p1-a").Take(3));
            running.Sideband.Complete();
            await running.Loop;
            ConversationEnded ended = await hook.WaitForAsync<ConversationEnded>();

            Assert.Equal((ConversationEndReason.Faulted, "sideband_closed"), (ended.Reason, ended.Call!.Cause));
        }

        // A sideband that breaks mid-line still hands the line it was hearing to the hooks, as every other end does.
        [Fact(Timeout = 30_000)]
        public async Task ASidebandThatFailsMidLineStillRaisesTheOpenLine()
        {
            RecordingHook hook = new();
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), [hook]);
            IReadOnlyList<string> log = LiveLog.Inbound("p1-a");
            int callerAsks = LiveLog.IndexOf(log, OpenAiLiveEvents.InputTranscriptDelta, nth: 3);

            running.Sideband.Push(log.Take(callerAsks));
            running.Sideband.Fail(new IOException("The socket was reset."));
            await running.Loop;
            ConversationEnded ended = await hook.WaitForAsync<ConversationEnded>();

            Assert.Equal(
                [(Speaker.Caller, "Hi there"), (Speaker.Agent, "Hey! Thanks for calling Sole Fitness. How can I help?")],
                hook.Of<LineSpoken>().Select(line => (line.Speaker, line.Text)));
            Assert.Equal((ConversationEndReason.Faulted, "sideband_failed"), (ended.Reason, ended.Call!.Cause));
        }

        // GPT-Live waits for the caller's voice unless it is told to speak first. The configured greeting goes out before
        // anything else as speakable commentary with no delegation: sent as session instructions, GPT-Live spoke it in
        // only 5 of 10 probe sessions (2026-10-06); as commentary, in 8 of 8, within about a second.
        [Fact(Timeout = 30_000)]
        public async Task TheCallTellsGptLiveToGreetTheCallerFirst()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), [], EndsAfterTheFirstAskYaml);

            JsonObject greet = await running.Sideband.WaitForSentAsync(sent => (string?)sent["content"] == RunningLiveCall.Greeting);

            Assert.Same(greet, running.Sideband.Sent[0]);
            Assert.Equal(OpenAiLiveEvents.CommentaryAppend, (string?)greet["type"]);
            Assert.True(greet.ContainsKey("delegation_id"));
            Assert.Null(greet["delegation_id"]);
            Assert.Equal(RunningLiveCall.Greeting, (string?)greet["content"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)greet["event_id"]));
        }

        // OpenAI's live-conversations guide: "send greeting instructions after session.started". Instructions sent
        // before it may land in a session that is not ready; a real call reported context_injection_incomplete.
        [Fact(Timeout = 30_000)]
        public async Task TheGreetingWaitsForTheSessionToStart()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), [], started: false);
            await running.Sideband.WaitForDrainAsync();
            bool sentBeforeTheStart = running.Sideband.Sent.Count > 0;

            running.Sideband.Push([RunningLiveCall.Started, RunningLiveCall.Started]);
            await running.Sideband.WaitForDrainAsync();

            Assert.False(sentBeforeTheStart);
            _ = Assert.Single(running.Sideband.Sent, sent => (string?)sent["content"] == RunningLiveCall.Greeting);
        }

        // A call whose conversation a newer call took is hung up, and says nothing.
        [Fact(Timeout = 30_000)]
        public async Task ACallThatLostItsConversationIsHungUpWithNoCommentary()
        {
            RecordingHook hook = new();
            using EventObservedLoggerProvider hungUpBecause = new("HungUpBecause");
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(
                new StallOnCueChatClient(), [hook, new CallDecider(gate => gate.Accept("thread-9"))], logger: hungUpBecause.CreateLogger("test"));
            await running.Harness.Sessions.CloseAsync("main", "thread-9", Ct);
            PhoneCall newer = (await PhoneCall.AdmitAsync(running.Harness.Host, PhoneCallHarness.Offer("call-8"), Ct)).Call!;
            await newer.StartAsync(Ct);
            IReadOnlyList<string> log = LiveLog.Inbound("p1-a");
            int delegation = LiveLog.IndexOf(log, OpenAiLiveEvents.DelegationCreated);

            running.Sideband.Push(log.Take(delegation + 1));
            _ = await running.HungUp.Task;
            running.Sideband.Push(log.Skip(delegation + 1));
            await running.Loop;
            await newer.Session.FlushNoticesAsync();

            Assert.True(running.Call.HasEnded);
            Assert.DoesNotContain(running.Sideband.Sent, IsCommentary);
            Assert.Empty(hook.Of<ConversationEnded>());
            Assert.Contains("conversation_lost", hungUpBecause.Message, StringComparison.Ordinal);
        }
    }
}

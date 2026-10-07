using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Tests.Calls
{
    /// <summary>
    /// A started call raises <see cref="CallEnded"/> once, however it leaves, and one that never started raises
    /// none, as it raised no <see cref="CallStarted"/>.
    /// </summary>
    public sealed class PhoneCallEndedNoticeTests
    {
        private static readonly DateTimeOffset Noon = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // The host's shutdown ends a call the same way a hang-up does, only with its own cause.
        [Theory]
        [InlineData(ConversationEndReason.CallerHungUp, "close_requested")]
        [InlineData(ConversationEndReason.Faulted, "shutdown")]
        public async Task ACallThatEndsItsConversationEndsOnceAfterConversationEnded(ConversationEndReason reason, string cause)
        {
            FakeTimeProvider time = new(Noon);
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), [hook], time: time);
            PhoneCall call = await StartedAsync(harness);
            time.Advance(TimeSpan.FromSeconds(30));

            await call.EndAsync(reason, cause);
            await call.CloseAsync();
            await call.Session.FlushNoticesAsync();

            Assert.Equal(("call-7", 30d, (string?)cause, CallEndReason.Ended), Shape(Assert.Single(hook.Of<CallEnded>())));
            Assert.Equal([typeof(ConversationEnded), typeof(CallEnded)], EndsInOrder(hook));
        }

        // The engine ended the conversation while the call stayed up; the hang-up after it still reaches the hooks.
        [Fact]
        public async Task ACallThatHangsUpAfterTheEngineEndedItsConversationStillEnds()
        {
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), [hook]);
            PhoneCall call = await StartedAsync(harness);
            _ = call.Session.EndConversation(ConversationEndReason.AgentCompleted);
            await call.Session.FlushNoticesAsync();

            await call.EndAsync(ConversationEndReason.AgentCompleted, "agent_ended");
            await call.Session.FlushNoticesAsync();

            CallEnded ended = Assert.Single(hook.Of<CallEnded>());
            Assert.Equal(("agent_ended", CallEndReason.Ended), (ended.Cause, ended.Reason));
        }

        // An end asked for while a turn runs waits for that turn; the call's end still comes after it.
        [Fact]
        public async Task AnEndThatWaitsOnTheRunningTurnStillComesBeforeTheCallsEnd()
        {
            RecordingHook hook = new();
            StallOnCueChatClient model = new() { HoldFirst = true };
            using PhoneCallHarness harness = PhoneCallHarness.Create(model, [hook]);
            PhoneCall call = await StartedAsync(harness);
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            Task turn = call.Session.RunTurnAsync("hi", stop.Token);
            await model.FirstEntered.Task.WaitAsync(Ct);

            await call.EndAsync(ConversationEndReason.CallerHungUp, "close_requested");
            await stop.CancelAsync();
            _ = await Record.ExceptionAsync(() => turn);
            await call.Session.FlushNoticesAsync();

            Assert.Equal([typeof(ConversationEnded), typeof(CallEnded)], EndsInOrder(hook));
        }

        [Fact]
        public async Task ACallThatLeavesWithoutAnEndIsClosedAndItsConversationGoesOnUnended()
        {
            FakeTimeProvider time = new(Noon);
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), [hook], time: time);
            PhoneCall call = await StartedAsync(harness);
            ConversationSession session = call.Session;
            time.Advance(TimeSpan.FromSeconds(4));

            await call.CloseAsync();
            await session.FlushNoticesAsync();

            Assert.Equal(("call-7", 4d, null, CallEndReason.Closed), Shape(Assert.Single(hook.Of<CallEnded>())));
            Assert.Empty(hook.Of<ConversationEnded>());
        }

        [Fact]
        public async Task ACallThatNeverStartedRaisesNoEnd()
        {
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), [hook]);
            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(), Ct)).Call!;
            ConversationSession session = call.Session;

            await call.AbandonAsync();
            await session.FlushNoticesAsync();

            Assert.Empty(hook.Of<CallStarted>());
            Assert.Empty(hook.Of<CallEnded>());
            Assert.Empty(hook.Of<ConversationEnded>());
        }

        // A takeover ends the older admission as replaced; the newer one ends normally later.
        [Fact]
        public async Task ATakeoverEndsTheOlderAdmissionAsReplacedAndTheNewerEndsNormallyLater()
        {
            FakeTimeProvider time = new(Noon);
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), [hook], time: time);
            PhoneCall older = await StartedAsync(harness);
            time.Advance(TimeSpan.FromSeconds(5));

            PhoneCall newer = await StartedAsync(harness);
            await older.EndAsync(ConversationEndReason.CallerHungUp, cause: null);
            await older.CloseAsync();
            time.Advance(TimeSpan.FromSeconds(7));
            await newer.EndAsync(ConversationEndReason.CallerHungUp, "close_requested");
            await newer.Session.FlushNoticesAsync();

            Assert.Equal(
                [("call-7", 5d, null, CallEndReason.Replaced), ("call-7", 12d, "close_requested", CallEndReason.Ended)],
                hook.Of<CallEnded>().Select(Shape));
        }

        // A call whose conversation another call took after an unload leaves as lost, and ends nothing of that call's.
        [Fact]
        public async Task ACallWhoseConversationAnotherCallTookLeavesAsLost()
        {
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), [hook, new CallDecider(gate => gate.Accept("thread-9"))]);
            PhoneCall stale = await StartedAsync(harness, "call-7");
            await harness.Sessions.CloseAsync("main", "thread-9", Ct);
            PhoneCall newer = await StartedAsync(harness, "call-8");

            await stale.EndAsync(ConversationEndReason.CallerHungUp, "close_requested");
            await stale.CloseAsync();
            await newer.Session.FlushNoticesAsync();

            CallEnded ended = Assert.Single(hook.Of<CallEnded>());
            Assert.Equal(("call-7", CallEndReason.Lost), (ended.CallId, ended.Reason));
            Assert.Empty(hook.Of<ConversationEnded>());
        }

        private static async Task<PhoneCall> StartedAsync(PhoneCallHarness harness, string callId = "call-7")
        {
            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(callId), Ct)).Call!;
            await call.StartAsync(Ct);
            return call;
        }

        private static (string CallId, double Seconds, string? Cause, CallEndReason Reason) Shape(CallEnded ended)
        {
            return (ended.CallId, ended.Seconds, ended.Cause, ended.Reason);
        }

        private static Type[] EndsInOrder(RecordingHook hook)
        {
            return [.. hook.Notices.Where(notice => notice is ConversationEnded or CallEnded).Select(notice => notice.GetType())];
        }
    }
}

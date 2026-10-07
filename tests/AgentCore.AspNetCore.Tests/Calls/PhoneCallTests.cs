using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Tests.Calls
{
    /// <summary>One call, whatever the vendor.</summary>
    public sealed class PhoneCallTests
    {
        private const string Brief = "Ask about the treadmill order.";

        private static readonly DateTimeOffset Noon = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // No hook accepts under the call id. The conversation starts before the call does.
        [Fact]
        public async Task WithNoHookTheCallJoinsAConversationNamedByItsCallIdAndStartsAfterIt()
        {
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), [hook]);

            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(), Ct)).Call!;
            await call.StartAsync(Ct);
            await call.Session.FlushNoticesAsync();

            Assert.Equal("call-7", call.ConversationId);
            Assert.Equal([typeof(ConversationStarted), typeof(CallStarted)], hook.Notices.Select(notice => notice.GetType()));
            Assert.Equal(ConversationOrigin.New, hook.Of<ConversationStarted>().Single().Origin);
            CallStarted started = hook.Of<CallStarted>().Single();
            Assert.Equal(("call-7", "+15550100", "+15550199", "test-transport"), (started.CallId, started.From, started.To, started.Transport));
        }

        [Fact]
        public async Task ARejectingHookRefusesTheCallAndOpensNothing()
        {
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), [new CallDecider(gate => gate.Reject(CallRefusal.Declined))]);

            PhoneCallAdmission admission = await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(), Ct);

            Assert.Equal(new PhoneCallAdmission(null, CallRefusal.Declined), admission);
            Assert.Null(await harness.Sessions.TryGetAsync("main", "call-7", Ct));
        }

        // An id another entry holds refuses the call as busy.
        [Fact]
        public async Task AnIdHeldByAnotherEntryRefusesTheCallAsBusy()
        {
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), [new CallDecider(gate => gate.Accept("thread-1"))]);
            _ = await harness.Sessions.GetOrOpenAsync("other", "thread-1", state: null, Ct);

            PhoneCallAdmission admission = await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(), Ct);

            Assert.Equal(CallRefusal.Busy, admission.Refusal);
        }

        // One live call per conversation: ending or letting go of one call must never end another's.
        [Fact]
        public async Task AConversationAnotherCallHoldsRefusesASecondCallAsBusy()
        {
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), [new CallDecider(gate => gate.Accept("thread-1"))]);
            PhoneCall first = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer("call-7"), Ct)).Call!;

            PhoneCallAdmission second = await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer("call-8"), Ct);

            Assert.Equal((null, (CallRefusal?)CallRefusal.Busy, true), (second.Call, second.Refusal, second.HeldByCall));
            Assert.Same(first.Session, await harness.Sessions.TryGetAsync("main", "thread-1", Ct));
        }

        // The brief reaches the engine as instructions while the call lives, and is gone after it.
        [Fact]
        public async Task TheBriefOfAnAcceptedCallReachesTheEngineUntilTheCallEnds()
        {
            InstructionsSeenChatClient model = new(new StallOnCueChatClient());
            using PhoneCallHarness harness = PhoneCallHarness.Create(model, [new CallDecider(gate => gate.Accept("thread-9", Brief))]);
            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(), Ct)).Call!;
            await call.StartAsync(Ct);

            _ = await call.Session.RunTurnAsync("hi", Ct);
            await call.EndAsync(ConversationEndReason.CallerHungUp, cause: null);

            Assert.Contains(Brief, model.Instructions[^1], StringComparison.Ordinal);
            await harness.Sessions.CloseAsync("main", "thread-9", Ct);

            // An ended conversation runs no further turn, so the later run is a new conversation under the same id.
            await call.Session.Compiled.ConversationStore.DeleteAsync("thread-9", Ct);
            Assert.DoesNotContain(Brief, await InstructionsOfALaterRunAsync(harness, model, "thread-9"), StringComparison.Ordinal);
        }

        // A call can leave without an end (a second setup frame closes it) or before the vendor took it.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ACallThatLeavesWithoutAnEndLeavesNoBriefOnTheConversation(bool vendorTookTheCall)
        {
            InstructionsSeenChatClient model = new(new StallOnCueChatClient());
            using PhoneCallHarness harness = PhoneCallHarness.Create(model, [new CallDecider(gate => gate.Accept("thread-9", Brief))]);
            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(), Ct)).Call!;

            if (vendorTookTheCall)
            {
                await call.StartAsync(Ct);
                await call.CloseAsync();
            }
            else
            {
                await call.AbandonAsync();
            }

            Assert.DoesNotContain(Brief, await InstructionsOfALaterRunAsync(harness, model, "thread-9"), StringComparison.Ordinal);
        }

        // The id moved on after an unload. The stale call's brief goes; a newer call owns the brief state.
        [Theory]
        [InlineData(false, null)]
        [InlineData(true, "Newer brief.")]
        [InlineData(true, null)]
        public async Task AStaleCallsBriefNeverReachesWhatNowHoldsTheConversation(bool aCallHoldsTheIdNow, string? newerBrief)
        {
            InstructionsSeenChatClient model = new(new StallOnCueChatClient());
            using PhoneCallHarness harness = PhoneCallHarness.Create(
                model, [new CallDecider(gate => gate.Accept("thread-9", gate.CallId == "call-7" ? "Stale brief." : newerBrief))]);
            PhoneCall stale = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer("call-7"), Ct)).Call!;
            await stale.StartAsync(Ct);
            await harness.Sessions.CloseAsync("main", "thread-9", Ct);
            if (aCallHoldsTheIdNow)
            {
                await (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer("call-8"), Ct)).Call!.StartAsync(Ct);
            }
            else
            {
                _ = await harness.Sessions.GetOrOpenAsync("main", "thread-9", state: null, Ct);
            }

            string before = await InstructionsOfALaterRunAsync(harness, model, "thread-9");
            await stale.EndAsync(ConversationEndReason.CallerHungUp, cause: null);
            string after = await InstructionsOfALaterRunAsync(harness, model, "thread-9");

            Assert.DoesNotContain("Stale brief.", after, StringComparison.Ordinal);
            Assert.Equal(newerBrief is not null, after.Contains("Newer brief.", StringComparison.Ordinal));
            if (aCallHoldsTheIdNow)
            {
                Assert.Equal(after, before);
            }
        }

        // One live call per conversation: a call whose conversation another call took after an unload has lost it.
        [Fact]
        public async Task ACallThatComesBackToAConversationALaterCallTookHasLostIt()
        {
            FakeTimeProvider time = new(Noon);
            InstructionsSeenChatClient model = new(new StallOnCueChatClient());
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(
                model, [hook, new CallDecider(gate => gate.Accept("thread-9", gate.CallId == "call-7" ? "Stale brief." : "Newer brief."))], time: time);
            PhoneCall stale = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer("call-7"), Ct)).Call!;
            await stale.StartAsync(Ct);
            await harness.Sessions.CloseAsync("main", "thread-9", Ct);
            PhoneCall newer = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer("call-8"), Ct)).Call!;
            await newer.StartAsync(Ct);
            time.Advance(TimeSpan.FromSeconds(12));

            _ = await Assert.ThrowsAsync<CallConversationLostException>(() => stale.LiveSessionAsync(Ct).AsTask());

            _ = await newer.Session.RunTurnAsync("hi", Ct);
            Assert.Contains("Newer brief.", model.Instructions[^1], StringComparison.Ordinal);
            await newer.EndAsync(ConversationEndReason.CallerHungUp, "close_requested");
            await newer.Session.FlushNoticesAsync();
            Assert.Equal(new CallEnd("call-8", 12, "close_requested"), Assert.Single(hook.Of<ConversationEnded>()).Call);
        }

        // One live call per conversation: an admitted call whose conversation unloaded starts on it again, unless a
        // later call took it.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AnAdmittedCallStartsOnItsReopenedConversationUnlessALaterCallTookIt(bool aCallTookIt)
        {
            InstructionsSeenChatClient model = new(new StallOnCueChatClient());
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(
                model, [hook, new CallDecider(gate => gate.Accept("thread-9", gate.CallId == "call-7" ? "Stale brief." : "Newer brief."))]);
            PhoneCall stale = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer("call-7"), Ct)).Call!;
            await harness.Sessions.CloseAsync("main", "thread-9", Ct);
            if (aCallTookIt)
            {
                await (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer("call-8"), Ct)).Call!.StartAsync(Ct);
                _ = await Assert.ThrowsAsync<CallConversationLostException>(() => stale.StartAsync(Ct).AsTask());
            }
            else
            {
                await stale.StartAsync(Ct);
                Assert.Same(await harness.Sessions.TryGetAsync("main", "thread-9", Ct), stale.Session);
            }

            string seen = await InstructionsOfALaterRunAsync(harness, model, "thread-9");
            Assert.Contains(aCallTookIt ? "Newer brief." : "Stale brief.", seen, StringComparison.Ordinal);
            Assert.DoesNotContain(aCallTookIt ? "Stale brief." : "Newer brief.", seen, StringComparison.Ordinal);
            await stale.Session.FlushNoticesAsync();
            Assert.Equal(aCallTookIt ? ["call-8"] : ["call-7"], hook.Of<CallStarted>().Select(started => started.CallId));
        }

        // The caller hung up while the store opened: the call never starts, so no brief is filed and no start is raised.
        [Fact]
        public async Task AnEndWhileTheStoreOpensStopsTheStart()
        {
            GatedCreateStore store = new();
            InstructionsSeenChatClient model = new(new StallOnCueChatClient());
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(model, [hook, new CallDecider(gate => gate.Accept("thread-9", Brief))], store: store);
            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(), Ct)).Call!;
            Task start = call.StartAsync(Ct).AsTask();
            await store.Entered.Task.WaitAsync(Ct);

            await call.EndAsync(ConversationEndReason.CallerHungUp, cause: null);
            store.Open.SetResult();

            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => start);
            await call.Session.FlushNoticesAsync();
            Assert.Empty(hook.Of<CallStarted>());
            await harness.Sessions.CloseAsync("main", "thread-9", Ct);
            await call.Session.Compiled.ConversationStore.DeleteAsync("thread-9", Ct);
            Assert.DoesNotContain(Brief, await InstructionsOfALaterRunAsync(harness, model, "thread-9"), StringComparison.Ordinal);
        }

        // An end that lands after the store opened, as the start stamps its time, still leaves no brief and no start.
        // The host clock is read once in the start, at its stamp, so its first read is the end.
        [Fact]
        public async Task AnEndAsTheStartStampsItsTimeLeavesNoBriefAndNoStart()
        {
            InstructionsSeenChatClient model = new(new StallOnCueChatClient());
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(model, [hook, new CallDecider(gate => gate.Accept("thread-9", Brief))]);
            PhoneCall? call = null;
            Task ending = Task.CompletedTask;
            PhoneCallHost host = harness.Host with { Time = new ActingTimeProvider(() => ending = call!.EndAsync(ConversationEndReason.CallerHungUp, cause: null).AsTask()) };
            call = (await PhoneCall.AdmitAsync(host, PhoneCallHarness.Offer(), Ct)).Call!;

            await call.StartAsync(Ct);
            await ending;

            await call.Session.FlushNoticesAsync();
            Assert.Empty(hook.Of<CallStarted>());
            await harness.Sessions.CloseAsync("main", "thread-9", Ct);
            await call.Session.Compiled.ConversationStore.DeleteAsync("thread-9", Ct);
            Assert.DoesNotContain(Brief, await InstructionsOfALaterRunAsync(harness, model, "thread-9"), StringComparison.Ordinal);
        }

        // A call starts once, and never after it left.
        [Theory]
        [InlineData("started")]
        [InlineData("abandoned")]
        [InlineData("ended")]
        public async Task AStartAfterTheCallStartedOrLeftThrows(string before)
        {
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), []);
            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(), Ct)).Call!;
            await (before switch
            {
                "started" => call.StartAsync(Ct),
                "abandoned" => call.AbandonAsync(),
                _ => call.EndAsync(ConversationEndReason.CallerHungUp, cause: null),
            });

            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => call.StartAsync(Ct).AsTask());
        }

        [Fact]
        public async Task HeardLinesBecomeLineSpokenInOrder()
        {
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), [hook]);
            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(), Ct)).Call!;
            await call.StartAsync(Ct);

            await call.HeardAsync(Speaker.Caller, "Hi there", Noon, Noon.AddSeconds(1));
            await call.HeardAsync(Speaker.Agent, "Hey! How can I help?", Noon.AddSeconds(2), Noon.AddSeconds(4));
            await call.Session.FlushNoticesAsync();

            Assert.Equal(
                [(Speaker.Caller, "Hi there", Noon, Noon.AddSeconds(1)), (Speaker.Agent, "Hey! How can I help?", Noon.AddSeconds(2), Noon.AddSeconds(4))],
                hook.Of<LineSpoken>().Select(line => (line.Speaker, line.Text, line.StartedAt, line.EndedAt)));
        }

        // After an unload, a heard line follows the call's conversation: onto a session no call holds, never into a later call's.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ALineHeardAfterAnUnloadNeverReachesALaterCallsConversation(bool aCallTookIt)
        {
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), [hook, new CallDecider(gate => gate.Accept("thread-9"))]);
            PhoneCall stale = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer("call-7"), Ct)).Call!;
            await stale.StartAsync(Ct);
            await harness.Sessions.CloseAsync("main", "thread-9", Ct);
            ConversationSession holder = aCallTookIt
                ? await StartedSessionAsync(harness, "call-8")
                : await harness.Sessions.GetOrOpenAsync("main", "thread-9", state: null, Ct);

            await stale.HeardAsync(Speaker.Caller, "Is anyone there?", Noon, Noon.AddSeconds(1));
            await holder.FlushNoticesAsync();

            Assert.Equal(aCallTookIt ? [] : ["Is anyone there?"], hook.Of<LineSpoken>().Select(line => line.Text));
            Assert.Equal(!aCallTookIt, ReferenceEquals(holder, stale.Session));
        }

        // Once per conversation, with the call's id, length, and the vendor's cause.
        [Fact]
        public async Task AnEndIsRaisedOnceWithTheCallsLengthAndCause()
        {
            FakeTimeProvider time = new(Noon);
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), [hook], time: time);
            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(), Ct)).Call!;
            await call.StartAsync(Ct);
            time.Advance(TimeSpan.FromSeconds(30));

            await call.EndAsync(ConversationEndReason.CallerHungUp, "close_requested");
            await call.EndAsync(ConversationEndReason.Faulted, "shutdown");
            await call.Session.FlushNoticesAsync();

            ConversationEnded ended = Assert.Single(hook.Of<ConversationEnded>());
            Assert.Equal(ConversationEndReason.CallerHungUp, ended.Reason);
            Assert.Equal(new CallEnd("call-7", 30, "close_requested"), ended.Call);
        }

        // A store that times out cancels with its own token, not the call's: the end and the close still go through,
        // on the call's own session, so the conversation is ended and released.
        [Fact]
        public async Task AStoreTimeoutDuringTheEndStillEndsAndReleasesTheConversation()
        {
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), [hook]);
            GatedConversationSessions store = new(harness.Sessions);
            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host with { Sessions = store }, PhoneCallHarness.Offer(), Ct)).Call!;
            await call.StartAsync(Ct);
            store.FaultTryGets(new TaskCanceledException("The store timed out."));

            await call.EndAsync(ConversationEndReason.CallerHungUp, "close_requested");
            await call.CloseAsync();
            await call.Session.FlushNoticesAsync();

            Assert.Equal(ConversationEndReason.CallerHungUp, Assert.Single(hook.Of<ConversationEnded>()).Reason);
            Assert.Null(await harness.Sessions.TryGetAsync("main", "call-7", Ct));
        }

        // The conversation reopened after an unload is still the call's, so its end names the call.
        [Fact]
        public async Task TheEndOfAReopenedSessionStillNamesTheCall()
        {
            FakeTimeProvider time = new(Noon);
            RecordingHook hook = new();
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), [hook], time: time);
            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(), Ct)).Call!;
            await call.StartAsync(Ct);
            await harness.Sessions.CloseAsync("main", "call-7", Ct);
            time.Advance(TimeSpan.FromSeconds(20));

            ConversationSession reopened = await call.LiveSessionAsync(Ct);
            await call.EndAsync(ConversationEndReason.CallerHungUp, "close_requested");
            await reopened.FlushNoticesAsync();

            Assert.Equal(new CallEnd("call-7", 20, "close_requested"), Assert.Single(hook.Of<ConversationEnded>()).Call);
        }

        private static async Task<ConversationSession> StartedSessionAsync(PhoneCallHarness harness, string callId)
        {
            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(callId), Ct)).Call!;
            await call.StartAsync(Ct);
            return call.Session;
        }

        private static async Task<string> InstructionsOfALaterRunAsync(PhoneCallHarness harness, InstructionsSeenChatClient model, string conversationId)
        {
            ConversationSession later = await harness.Sessions.GetOrOpenAsync("main", conversationId, state: null, Ct);
            _ = await later.RunTurnAsync("hello again", Ct);
            return model.Instructions[^1];
        }
    }
}

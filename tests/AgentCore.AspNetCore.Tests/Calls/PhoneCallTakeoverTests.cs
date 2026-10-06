using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Calls
{
    /// <summary>
    /// LiveKit's duplicate-identity rule: a newer call with the same call id takes the
    /// conversation over, and the older call is told and can no longer end or close it.
    /// </summary>
    public sealed class PhoneCallTakeoverTests
    {
        private const string Brief = "Ask about the treadmill order.";

        private static readonly DateTimeOffset Noon = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task ANewerCallWithTheSameIdTakesTheConversationOverAndTheOlderOneCannotEndIt()
        {
            FakeTimeProvider time = new(Noon);
            RecordingHook hook = new();
            InstructionsSeenChatClient model = new(new StallOnCueChatClient());
            using PhoneCallHarness harness = PhoneCallHarness.Create(model, [hook, new CallDecider(gate => gate.Accept("thread-9", Brief))], time: time);
            List<PhoneCall> told = [];
            PhoneCall older = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer("call-7"), Ct, told.Add)).Call!;
            await older.StartAsync(Ct);
            time.Advance(TimeSpan.FromSeconds(5));

            PhoneCall newer = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer("call-7"), Ct)).Call!;
            await newer.StartAsync(Ct);
            await older.EndAsync(ConversationEndReason.CallerHungUp, cause: null);
            await older.CloseAsync();

            Assert.Equal([older], told);
            Assert.Same(newer.Session, await harness.Sessions.TryGetAsync("main", "thread-9", Ct));
            Assert.False(newer.Session.Lifetime.Ending.Requested);
            _ = await newer.Session.RunTurnAsync("hi", Ct);
            Assert.Contains(Brief, model.Instructions[^1], StringComparison.Ordinal);

            time.Advance(TimeSpan.FromSeconds(7));
            await newer.EndAsync(ConversationEndReason.CallerHungUp, "close_requested");
            await newer.Session.FlushNoticesAsync();

            // One call: one start and one end, its length counted from the older connection's start.
            Assert.Equal(new CallEnd("call-7", 12, "close_requested"), Assert.Single(hook.Of<ConversationEnded>()).Call);
            _ = Assert.Single(hook.Of<CallStarted>());
        }

        // A call that already left its conversation (its end or close began) is not taken over: the conversation is
        // ending, so a reconnect is refused as busy until the close is done.
        [Fact]
        public async Task ACallThatAlreadyEndedIsNotTakenOver()
        {
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), []);
            PhoneCall older = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer("call-7"), Ct)).Call!;
            await older.StartAsync(Ct);
            await older.EndAsync(ConversationEndReason.CallerHungUp, cause: null);

            PhoneCallAdmission newer = await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer("call-7"), Ct);

            Assert.Equal(CallRefusal.Busy, newer.Refusal);
        }
    }
}

using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Layers;
using AgentCore.Application.Hooks.Notices;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class CallGateTests
    {
        private static readonly CallOffer Offer = new(
            "main", "call-7", "+15550100", "+15550199", new Dictionary<string, string>(StringComparer.Ordinal) { ["Diversion"] = "x" }, "telnyx-relay");

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // With no handler, the call id is the conversation id.
        [Fact]
        public async Task WithNoHookTheCallIsAcceptedUnderItsOwnId()
        {
            CallDecision decision = await CallGateChain.DecideAsync(HookRuntime.Create([], loggers: null), Offer, Ct);

            Assert.Equal(new CallDecision(true, "call-7", null, null), decision);
        }

        [Fact]
        public async Task AnAcceptingHookNamesTheConversationAndABriefCutToAThousandCharacters()
        {
            string longBrief = new('b', 1500);
            Calls hook = new(gate =>
            {
                Assert.Equal(("+15550100", "x"), (gate.From, gate.Headers["Diversion"]));
                gate.Accept("thread-42", longBrief);
            });

            CallDecision decision = await CallGateChain.DecideAsync(HookRuntime.Create([hook], loggers: null), Offer, Ct);

            Assert.True(decision.Accepted);
            Assert.Equal("thread-42", decision.ConversationId);
            Assert.Equal(1000, decision.Brief!.Length);
        }

        [Fact]
        public async Task ARejectingHookRefusesWithItsReason()
        {
            CallDecision decision = await CallGateChain.DecideAsync(
                HookRuntime.Create([new Calls(gate => gate.Reject(CallRefusal.Busy))], loggers: null), Offer, Ct);

            Assert.Equal(new CallDecision(false, null, null, CallRefusal.Busy), decision);
        }

        // "no verb called" rejects as Unavailable.
        [Fact]
        public async Task HooksThatDecideNothingRejectTheCallAsUnavailable()
        {
            CallDecision decision = await CallGateChain.DecideAsync(HookRuntime.Create([new Calls(_ => { })], loggers: null), Offer, Ct);

            Assert.Equal(CallRefusal.Unavailable, decision.Refusal);
        }

        // The safe verb applies, and the other hooks hear of the failure.
        [Fact]
        public async Task AThrowingHookFailsClosedAndTheOtherHooksHearOfIt()
        {
            RecordingHook notices = new();
            HookRuntime runtime = HookRuntime.Create([new Calls(_ => throw new InvalidOperationException("crm down")), notices], loggers: null);

            CallDecision decision = await CallGateChain.DecideAsync(runtime, Offer, Ct);
            await runtime.Notices.FlushAsync(conversationId: null);

            Assert.Equal(CallRefusal.Unavailable, decision.Refusal);
            Fault fault = Assert.Single(notices.Of<Fault>());
            Assert.Equal(FaultKind.HookFailed, fault.Kind);
        }

        // Closed ends the chain at the failure: a later hook cannot accept a call the gate could not vet.
        [Fact]
        public async Task AFailedHookEndsTheChainSoALaterHookCannotAcceptTheCall()
        {
            HookRuntime runtime = HookRuntime.Create(
                [new Calls(_ => throw new InvalidOperationException("crm down")), new Calls(gate => gate.Accept("thread-42"))], loggers: null);

            CallDecision decision = await CallGateChain.DecideAsync(runtime, Offer, Ct);

            Assert.Equal(new CallDecision(false, null, null, CallRefusal.Unavailable), decision);
        }

        [Fact(Timeout = 10_000)]
        public async Task AHookThatMissesTheFiveSecondDeadlineFailsClosed()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            Calls stuck = new(_ => { }) { Delay = (Timeout.InfiniteTimeSpan, TimeProvider.System) };
            HookRuntime runtime = HookRuntime.Create([stuck], loggers: null, timers: time);

            Task<CallDecision> deciding = CallGateChain.DecideAsync(runtime, Offer, Ct).AsTask();
            await time.WaitForTimersAsync(time.GetUtcNow() + GatePoint.BeforeCall.Deadline, 2);
            time.Advance(GatePoint.BeforeCall.Deadline);

            Assert.Equal(CallRefusal.Unavailable, (await deciding).Refusal);
        }

        // Providers.conversation.answerSeconds is the deadline of the call gate.
        [Fact(Timeout = 10_000)]
        public async Task AShorterAnswerDeadlineRejectsAStuckHookSooner()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            Calls stuck = new(_ => { }) { Delay = (Timeout.InfiniteTimeSpan, TimeProvider.System) };
            HookRuntime runtime = HookRuntime.Create([stuck], loggers: null, timers: time);
            TimeSpan answer = TimeSpan.FromSeconds(2);

            Task<CallDecision> deciding = CallGateChain.DecideAsync(runtime, Offer, Ct, answer).AsTask();
            await time.WaitForTimersAsync(time.GetUtcNow() + answer, 2);
            time.Advance(answer);

            Assert.Equal(CallRefusal.Unavailable, (await deciding).Refusal);
        }

        [Fact(Timeout = 10_000)]
        public async Task ALongerAnswerDeadlineWaitsForAHookTheDefaultWouldHaveAbandoned()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            TimeSpan hookTakes = TimeSpan.FromSeconds(5.5);
            Calls slow = new(gate => gate.Accept("thread-9")) { Delay = (hookTakes, time) };
            HookRuntime runtime = HookRuntime.Create([slow], loggers: null, timers: time);
            TimeSpan answer = TimeSpan.FromSeconds(6);

            Task<CallDecision> deciding = CallGateChain.DecideAsync(runtime, Offer, Ct, answer).AsTask();
            await time.WaitForTimersAsync(time.GetUtcNow() + answer, 2);
            await time.WaitForTimersAsync(time.GetUtcNow() + hookTakes, 1);
            time.Advance(hookTakes);

            Assert.Equal(new CallDecision(true, "thread-9", null, null), await deciding);
        }

        private sealed class Calls(Action<CallGate> decide) : AgentHook
        {
            public (TimeSpan Wait, TimeProvider Clock)? Delay { get; init; }

            public override async ValueTask BeforeCallAsync(CallGate gate, CancellationToken cancellationToken)
            {
                if (Delay is { } delay)
                {
                    await Task.Delay(delay.Wait, delay.Clock, CancellationToken.None);
                }

                decide(gate);
            }
        }
    }
}

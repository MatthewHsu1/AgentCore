using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>The end of a call's conversation names the call.</summary>
    public sealed class CallEndNoticeTests
    {
        private static readonly DateTimeOffset Noon = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task TheEndOfACallNamesTheCallItsLengthAndTheCause()
        {
            FakeTimeProvider time = new(Noon);
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("hello");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook], time: time);
            Assert.True(session.Lifetime.Ending.TryMarkCall("call-7", static () => { }, time.GetUtcNow()));
            time.Advance(TimeSpan.FromSeconds(42));

            Assert.True(session.Lifetime.EndConversation(ConversationEndReason.CallerHungUp, "close_requested"));
            await session.FlushNoticesAsync();

            ConversationEnded ended = Assert.Single(hook.Of<ConversationEnded>());
            Assert.Equal(ConversationEndReason.CallerHungUp, ended.Reason);
            Assert.Equal(new CallEnd("call-7", 42, "close_requested"), ended.Call);
        }

        // A call's length runs to the hang-up, not to the raise that waited on the running turn.
        [Fact(Timeout = 30_000)]
        public async Task AnEndAskedForDuringATurnMeasuresTheCallUpToTheAsk()
        {
            FakeTimeProvider time = new(Noon);
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("Hello", " there") { GateAfterFirstFragment = true };
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook], time: time);
            Assert.True(session.Lifetime.Ending.TryMarkCall("call-9", static () => { }, time.GetUtcNow()));
            await using IAsyncEnumerator<ChatResponseUpdate> stream = session.RunTurnStreamingAsync("hi", Ct).GetAsyncEnumerator(Ct);
            Assert.True(await stream.MoveNextAsync());
            time.Advance(TimeSpan.FromSeconds(10));

            _ = session.Lifetime.EndConversation(ConversationEndReason.CallerHungUp, "close_requested");
            await time.WaitForTimersAsync(time.GetUtcNow() + ConversationEnding.TurnWait, 1);
            time.Advance(ConversationEnding.TurnWait);
            ConversationEnded ended = await hook.WaitForAsync<ConversationEnded>().WaitAsync(TimeSpan.FromSeconds(10), Ct);

            Assert.Equal(new CallEnd("call-9", 10, "close_requested"), ended.Call);
            reply.OpenGate();
            while (await stream.MoveNextAsync())
            {
            }
        }

        // An end the agent makes itself still names the call, with no cause of the vendor's.
        [Fact]
        public async Task ATerminalStageEndOfACallNamesTheCallWithNoCause()
        {
            FakeTimeProvider time = new(Noon);
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("goodbye");
            ConversationSession session = HookSessions.Create(HookSessions.EndsAfterTheFirstTurnYaml, reply, [hook], time: time);
            Assert.True(session.Lifetime.Ending.TryMarkCall("call-8", static () => { }, time.GetUtcNow()));
            time.Advance(TimeSpan.FromSeconds(5));

            _ = await session.RunTurnAsync("hi", Ct);
            await session.FlushNoticesAsync();

            ConversationEnded ended = Assert.Single(hook.Of<ConversationEnded>());
            Assert.Equal(ConversationEndReason.AgentCompleted, ended.Reason);
            Assert.Equal(new CallEnd("call-8", 5, null), ended.Call);
        }
    }
}

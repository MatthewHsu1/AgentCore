using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class CutAndFaultNoticeTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // The cut amends the TurnCompleted it follows.
        [Fact]
        public async Task ACutDuringTheReplyAmendsTheTurnItCut()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("Hello", " there", " caller") { GateAfterFirstFragment = true };
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook]);

            await using IAsyncEnumerator<ChatResponseUpdate> stream = session.RunTurnStreamingAsync("hi", Ct).GetAsyncEnumerator(Ct);
            Assert.True(await stream.MoveNextAsync());
            Assert.True(session.Cut(0, new TurnCut("Hello", TimeSpan.FromMilliseconds(300))));
            reply.OpenGate();
            while (await stream.MoveNextAsync())
            {
            }

            await session.FlushNoticesAsync();

            TurnCompleted completed = Assert.Single(hook.Of<TurnCompleted>());
            ReplyCut cut = Assert.Single(hook.Of<ReplyCut>());
            Assert.Equal("Hello", cut.HeardText);
            Assert.Equal(TimeSpan.FromMilliseconds(300), cut.Played);
            Assert.Equal(completed.EventId, cut.AmendsEventId);
            Assert.True(completed.Scope.Sequence < cut.Scope.Sequence);
        }

        [Fact]
        public async Task ACutAfterTheSealAmendsTheTurnLate()
        {
            RecordingHook hook = new();
            using ScriptedChatClient reply = new("Hello there");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook]);
            _ = await session.RunTurnAsync("hi", Ct);

            Assert.True(session.Cut(0, new TurnCut("Hel", TimeSpan.FromMilliseconds(100))));
            await session.FlushNoticesAsync();

            ReplyCut cut = Assert.Single(hook.Of<ReplyCut>());
            Assert.Equal(("Hel", (TimeSpan?)TimeSpan.FromMilliseconds(100)), (cut.HeardText, cut.Played));
            Assert.Equal(hook.Of<TurnCompleted>().Single().EventId, cut.AmendsEventId);
        }

        // Turn indexes copied from ConversationSessionStoreFailureTests (write at 0, resync at 1).
        [Fact]
        public async Task AStoreThatRefusesWritesAndReadsRaisesTwoFaults()
        {
            RecordingHook hook = new();
            using RequestRecordingChatClient reply = new("hi there", "it ships Friday");
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, reply, [hook], new ThrowingConversationStore());

            _ = await session.RunTurnAsync("hello", Ct);
            _ = await session.RunTurnAsync("order 41?", Ct);
            await session.FlushTranscriptAsync();
            await session.FlushNoticesAsync();

            Fault written = hook.Of<Fault>().First(f => f.Kind == FaultKind.TranscriptWriteFailed);
            Fault resync = Assert.Single(hook.Of<Fault>(), f => f.Kind == FaultKind.TranscriptResyncFailed);
            Assert.Equal(0, written.Scope.TurnIndex);
            Assert.Equal(1, resync.Scope.TurnIndex);
            Assert.NotNull(resync.Cause);
        }
    }
}

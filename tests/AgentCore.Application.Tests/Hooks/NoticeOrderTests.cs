using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class NoticeOrderTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // One conversation's notices come from the turn thread, the store's write queue (the transcript
        // write faults), two voice-like threads, and a reloaded session. (SessionId, Sequence) is gap-free and causal.
        [Fact(Timeout = 60_000)]
        public async Task NoticesFromEveryThreadAndAReloadAreGapFreeAndCausal()
        {
            RecordingHook hook = new();
            using RequestRecordingChatClient reply = new("one", "two", "three", "four");
            CompiledAgent compiled = HookSessions.Compile(HookSessions.OneAgentYaml, reply, [hook], new ThrowingConversationStore())["main"];
            ConversationSessionFactory factory = new(compiled, new GuardEvaluator(compiled.Configuration.Guards));

            ConversationSession first = factory.Create("c1");
            Task<TurnResult> firstTurn = first.RunTurnAsync("turn 0", Ct);

            // The voice threads start once the session opened, so the session's start is still its first notice.
            // The user's and the agent's states change on their own threads until the session's last turn ends,
            // so their raises overlap each other, the turns and the write queue.
            _ = await hook.WaitForAsync<ConversationStarted>();
            using CancellationTokenSource turnsDone = new();
            int[] changes = new int[2];
            Thread[] voices = [.. new[] { VoiceParty.User, VoiceParty.Agent }.Select(party => new Thread(() =>
            {
                while (!turnsDone.IsCancellationRequested)
                {
                    bool listening = changes[(int)party] % 2 == 0;
                    _ = first.Hooks.Raise(new VoiceStateChanged(
                        first.Hooks.Scope(turnIndex: null, stage: null),
                        party,
                        listening ? VoiceState.Listening : VoiceState.Speaking,
                        listening ? VoiceState.Speaking : VoiceState.Listening));
                    changes[(int)party]++;
                }
            }))];
            Array.ForEach(voices, static voice => voice.Start());
            _ = await firstTurn;
            for (int turn = 1; turn < 3; turn++)
            {
                _ = await first.RunTurnAsync($"turn {turn}", Ct);
            }

            await turnsDone.CancelAsync();
            Array.ForEach(voices, static voice => voice.Join());
            await first.FlushTranscriptAsync();
            await first.Lifetime.DisposeAsync(UnloadCause.Idle);

            ConversationSession second = factory.Create("c1");
            _ = await second.RunTurnAsync("after the reload", Ct);
            await second.FlushTranscriptAsync();
            await second.FlushNoticesAsync();

            IReadOnlyList<HookNotice> notices = hook.Notices;

            // Gap-free: the mailbox numbers every notice of the conversation from 1, across both sessions.
            Assert.Equal(Enumerable.Range(1, notices.Count).Select(value => (long)value), notices.Select(notice => notice.Scope.Sequence));

            // SessionId sorts by load time, so it never goes backwards in delivery order.
            Assert.All(notices.Zip(notices.Skip(1)), pair => Assert.True(pair.First.Scope.SessionId.CompareTo(pair.Second.Scope.SessionId) <= 0));
            Assert.Equal(2, notices.Select(notice => notice.Scope.SessionId).Distinct().Count());

            // Causal: each session starts first and the first session's unload is its last notice.
            Assert.All(
                notices.GroupBy(notice => notice.Scope.SessionId),
                session => Assert.IsType<ConversationStarted>(session.First()));
            Assert.Equal([ConversationOrigin.New, ConversationOrigin.Reloaded], hook.Of<ConversationStarted>().Select(started => started.Origin));
            ConversationUnloaded unloaded = Assert.Single(hook.Of<ConversationUnloaded>());
            Assert.Equal(UnloadCause.Idle, unloaded.Cause);
            Assert.Equal(hook.Of<ConversationStarted>()[0].Scope.SessionId, unloaded.Scope.SessionId);
            Assert.Same(unloaded, notices.Last(notice => notice.Scope.SessionId == unloaded.Scope.SessionId));

            // Each turn starts before it completes, and before the write queue reports its failed store write.
            Assert.Equal(4, hook.Of<TurnCompleted>().Count);
            foreach (HookNotice after in hook.Of<TurnCompleted>().Concat<HookNotice>(hook.Of<Fault>().Where(fault => fault.Kind == FaultKind.TranscriptWriteFailed)))
            {
                TurnStarted started = hook.Of<TurnStarted>().Single(s => s.Scope.SessionId == after.Scope.SessionId && s.Scope.TurnIndex == after.Scope.TurnIndex);
                Assert.True(started.Scope.Sequence < after.Scope.Sequence, $"{after} came before its turn started");
            }

            Assert.Equal(changes.Sum(), hook.Of<VoiceStateChanged>().Count);
            Assert.Contains(hook.Of<Fault>(), fault => fault.Kind == FaultKind.TranscriptWriteFailed);
        }
    }
}

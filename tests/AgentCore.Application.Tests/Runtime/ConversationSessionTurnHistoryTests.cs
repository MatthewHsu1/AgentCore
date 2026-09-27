using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Runtime.ConversationSessionCutTestSupport;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// What a turn's model reads when the turn starts beside the one before it. The rule is LiveKit's, which the
    /// voice port follows: the next reply starts only after the previous one is done and saved
    /// (agent_activity.py:2782). So a turn's model input holds every turn committed before it was admitted,
    /// and a start that gives up waiting changes nothing.
    /// </summary>
    public sealed class ConversationSessionTurnHistoryTests
    {
        private static readonly TimeSpan Stuck = TimeSpan.FromSeconds(10);

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // Probe P7e (docs/probes/voice-engine/engine/REPORT.md, F1): the next turn's start reads the store while the
        // turn before it is cut and commits. The cut turn ends only once the store answered its append
        // (ConversationTurnCommit.RefusedAsync), so the append lands before that read returns.
        [Fact]
        public async Task NextTurnStartsWhileThePreviousTurnCommits_ItsModelReadsThePreviousTurn()
        {
            // Arrange
            HoldingConversationStore store = new();
            TurnScriptChatClient model = TurnScriptChatClient.Sequence(["first reply"], ["second reply"]);
            model.GateBeforeCall = 0;
            ConversationSession session = Create(model, store);
            TurnRun first = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "q0"), origin: null, Ct);
            Task firstRead = ReadToEndAsync(first, Ct);
            await model.Gated.Task.WaitAsync(Stuck, Ct);

            // Act
            store.HoldGets();
            store.HoldAppends();
            Task<TurnRun> starting = session.StartTurnAsync(new ChatMessage(ChatRole.User, "q1"), origin: null, Ct);
            await store.GetHeld.Task.WaitAsync(Stuck, Ct);
            Assert.True(session.Cut(first.TurnIndex, new TurnCut(string.Empty, null)));
            await store.AppendHeld.Task.WaitAsync(Stuck, Ct);
            store.ReleaseAppends();
            await firstRead.WaitAsync(Stuck, Ct);
            await first.DisposeAsync();

            store.ReleaseGets();
            await using TurnRun second = await starting.WaitAsync(Stuck, Ct);
            await ReadToEndAsync(second, Ct).WaitAsync(Stuck, Ct);
            await session.FlushTranscriptAsync();

            // Assert
            Assert.Equal(["q0", "q1"], Texts(model.Requests[1].Where(message => message.Role != ChatRole.System)));
            Assert.Equal(["q0", "q1", "second reply"], Texts(session.Transcript));
        }

        // Probe P7a/P7c, the other order: the store read begun before the turn was admitted misses the turn that
        // committed meanwhile, since the session's words moved under it, and the store also holds a row written from
        // outside any turn. The admitted turn reads both.
        [Fact]
        public async Task StoreReadBeforeAdmissionMissesTheTurnThatCommitted_TheAdmittedTurnStillReadsEveryRow()
        {
            // Arrange
            HoldingConversationStore store = new();
            TurnScriptChatClient model = TurnScriptChatClient.Sequence(["a0"], ["a1"], ["a2"]);
            model.GateBeforeCall = 1;
            ConversationSession session = Create(model, store);
            _ = await session.RunTurnAsync("q0", Ct);
            TurnRun running = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "q1"), origin: null, Ct);
            Task runningRead = ReadToEndAsync(running, Ct);
            await model.Gated.Task.WaitAsync(Stuck, Ct);
            _ = await ((IConversationStore)store).AppendMessageAsync(session.ConversationId, new ChatMessage(ChatRole.User, "a note"), Ct);

            // Act
            store.HoldGets();
            store.HoldAppends();
            Task<TurnRun> starting = session.StartTurnAsync(new ChatMessage(ChatRole.User, "q2"), origin: null, Ct);
            await store.GetHeld.Task.WaitAsync(Stuck, Ct);
            _ = model.Release.TrySetResult();
            await store.AppendHeld.Task.WaitAsync(Stuck, Ct);
            store.ReleaseAppends();
            await runningRead.WaitAsync(Stuck, Ct);
            await running.DisposeAsync();

            store.ReleaseGets();
            await store.SessionRead.Task.WaitAsync(Stuck, Ct);
            await using TurnRun third = await starting.WaitAsync(Stuck, Ct);
            await ReadToEndAsync(third, Ct).WaitAsync(Stuck, Ct);
            await session.FlushTranscriptAsync();

            // Assert
            Assert.Equal(
                ["q0", "a0", "a note", "q1", "a1", "q2"],
                Texts(model.Requests[2].Where(message => message.Role != ChatRole.System)));
        }

        // Probe F1, second cause: a start that gives up waiting for a running turn must not re-read the store into the
        // session. The next admitted turn still catches up with what was written from outside.
        [Fact]
        public async Task AStartAbandonedWhileATurnRuns_ChangesNoWords_AndTheNextTurnCatchesUp()
        {
            // Arrange
            TurnScriptChatClient model = TurnScriptChatClient.Sequence(["a0"], ["a1"], ["a2"]);
            model.GateBeforeCall = 1;
            (ConversationSession session, RecordingConversationStore store, _) = Create(model);
            _ = await session.RunTurnAsync("q0", Ct);
            TurnRun running = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "q1"), origin: null, Ct);
            Task runningRead = ReadToEndAsync(running, Ct);
            await model.Gated.Task.WaitAsync(Stuck, Ct);
            _ = await ((IConversationStore)store).AppendMessageAsync(session.ConversationId, new ChatMessage(ChatRole.User, "a note"), Ct);

            // Act
            using (CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(Ct))
            {
                giveUp.CancelAfter(TimeSpan.FromMilliseconds(200));
                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => session.StartTurnAsync(new ChatMessage(ChatRole.User, "q2"), origin: null, giveUp.Token));
            }

            List<string> afterRefusal = Texts(session.Transcript);

            _ = model.Release.TrySetResult();
            await runningRead.WaitAsync(Stuck, Ct);
            await running.DisposeAsync();
            _ = await session.RunTurnAsync("q2", Ct);

            // Assert
            Assert.Equal(["q0", "a0"], afterRefusal);
            Assert.Equal(
                ["q0", "a0", "a note", "q1", "a1", "q2"],
                Texts(model.Requests[2].Where(message => message.Role != ChatRole.System)));
        }
    }
}

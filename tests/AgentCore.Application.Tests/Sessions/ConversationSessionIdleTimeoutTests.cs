using AgentCore.Application.Runtime;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Tests.Runtime;
using AgentCore.TestSupport;
using AgentCore.Domain;
using Xunit;
using static AgentCore.Application.Tests.Sessions.ConversationSessionsFixture;

namespace AgentCore.Application.Tests.Sessions
{
    /// <summary>
    /// A held session unloads itself once it has been untouched for the idle timeout, on its own timer.
    /// </summary>
    public sealed class ConversationSessionIdleTimeoutTests
    {
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

        [Fact]
        public async Task ASessionNobodyTouchesClosesItselfAtTheIdleTimeout()
        {
            FakeTimeProvider clock = Clock();
            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, clock);
            _ = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);

            clock.Advance(IdleTimeout - TimeSpan.FromSeconds(1));
            Assert.Equal(1, sessions.Count);

            // A caller that abandons a text conversation never reaches a terminal stage, so nothing else would
            // ever drop this session and the process would hold it for its whole life.
            clock.Advance(TimeSpan.FromSeconds(1));

            Assert.Equal(0, sessions.Count);
            Assert.Null(await sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Token));
        }

        [Fact]
        public void TheDefaultIdleTimeoutIsTenMinutes()
        {
            Assert.Equal(TimeSpan.FromMinutes(10), InMemoryConversationSessions.DefaultIdleTimeout);
        }

        [Fact]
        public async Task AnExpiringSessionWritesNoConversationEndedEventAndTheSessionIsDisposed()
        {
            // An idle unload frees the session's resources without ending the conversation: no
            // conversation.ended event, so the conversation stays open in the store for a caller to resume.
            RecordingConversationObserver observer = new();
            FakeTimeProvider clock = Clock();
            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory(observer: observer)), IdleTimeout, clock);
            ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);

            clock.Advance(IdleTimeout);

            await EventuallyAsync(() => IsDisposed(session));
            Assert.DoesNotContain(ConversationEventKind.ConversationEnded, observer.Kinds);
        }

        [Fact]
        public async Task ReadingASessionPutsItsIdleClockBackToZero()
        {
            FakeTimeProvider clock = Clock();
            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, clock);
            _ = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);

            clock.Advance(TimeSpan.FromMinutes(20));
            Assert.NotNull(await sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Token));

            // Forty minutes since it was opened, twenty since it was last read. A conversation still being had
            // must not be dropped out from under the caller.
            clock.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(1, sessions.Count);

            clock.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal(0, sessions.Count);
        }

        [Fact]
        public async Task ASessionWhoseTurnIsRunningIsKeptAndClosesTheIdleTimeoutAfterTheTurnEnds()
        {
            // The turn is waiting on its own write. Expiring now would dispose the session under a turn
            // whose words have not landed yet.
            ParkingConversationStore transcript = new();
            FakeTimeProvider clock = Clock();
            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory(transcript)), IdleTimeout, clock);
            ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);

            Task<TurnResult> turn = session.RunTurnAsync("hello", Token);
            await transcript.Parked;

            clock.Advance(IdleTimeout);
            clock.Advance(IdleTimeout);
            Assert.Equal(1, sessions.Count);
            Assert.False(IsDisposed(session));

            transcript.Release();
            _ = await turn;
            Assert.True(transcript.Landed);
            await clock.WaitForTimersAsync(clock.GetUtcNow() + IdleTimeout, 1).WaitAsync(TimeSpan.FromSeconds(10), Token);

            clock.Advance(IdleTimeout - TimeSpan.FromSeconds(1));
            Assert.Equal(1, sessions.Count);

            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(0, sessions.Count);
            await EventuallyAsync(() => IsDisposed(session));
        }

        [Fact]
        public async Task ATurnRunningPastTheIdleTimeoutStampsTheWorkspaceOnceItCrossesTheTimeout()
        {
            // A turn alone (no background child) holds the session past its idle timeout. The moment the idle
            // poll first finds the turn still running is stamped, so another server's boot sweep does not
            // mistake this folder for one a crashed process left behind.
            string root = Directory.CreateTempSubdirectory("agentcore-ws-turn-").FullName;
            try
            {
                ParkingConversationStore transcript = new();
                FakeTimeProvider clock = Clock();
                using InMemoryConversationSessions sessions = new(
                    SingleEntrySessionFactories.Of(Factory(transcript, workspaceRoot: root, timeProvider: clock)), IdleTimeout, clock);
                ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);

                Task<TurnResult> turn = session.RunTurnAsync("hello", Token);
                await transcript.Parked;

                DateTime stampAtTurnStart = Directory.GetLastWriteTimeUtc(session.Workspace!);

                clock.Advance(IdleTimeout);

                Assert.Equal(1, sessions.Count);
                Assert.Equal(clock.GetUtcNow().UtcDateTime, Directory.GetLastWriteTimeUtc(session.Workspace!));
                Assert.NotEqual(stampAtTurnStart, Directory.GetLastWriteTimeUtc(session.Workspace!));

                transcript.Release();
                _ = await turn;
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task AStuckTurnInOneSessionDoesNotStopOtherSessionsFromExpiring()
        {
            // Review finding R5-2. Closing a session waits for its running turn, so a close that one session
            // cannot finish must hold that session only.
            FakeTimeProvider clock = Clock();
            using HangUntilCancelledChatClient model = new();
            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory(reply: model)), IdleTimeout, clock);

            ConversationSession busy = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "a-busy", null, Token);
            for (int i = 0; i < 20; i++)
            {
                _ = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "idle-" + i, null, Token);
            }

            using CancellationTokenSource turnCancel = new();
            Task<TurnResult> turn = busy.RunTurnAsync("hello", turnCancel.Token);
            await model.Started.WaitAsync(TimeSpan.FromSeconds(10), Token);

            clock.Advance(IdleTimeout + TimeSpan.FromMinutes(1));
            int held = sessions.Count;

            await turnCancel.CancelAsync();
            _ = await Record.ExceptionAsync(() => turn);

            Assert.Equal(1, held);
            Assert.Same(busy, await sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "a-busy", Token));
        }

        [Fact]
        public async Task ALookupThatTouchesTheSessionAfterItsTimerCameDueKeepsTheSession()
        {
            // The timer came due and its callback is on its way to a thread when the request arrives. The
            // request is live, so the callback must see the touch and stand down.
            FakeTimeProvider clock = Clock();
            DeferredTimeProvider deferred = new(clock);
            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, deferred);
            ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);

            clock.Advance(IdleTimeout);
            Assert.Same(session, await sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Token));
            Assert.Equal(1, deferred.RunDue());

            Assert.Equal(1, sessions.Count);
            Assert.False(IsDisposed(session));

            clock.Advance(IdleTimeout);
            Assert.Equal(1, deferred.RunDue());
            Assert.Equal(0, sessions.Count);
        }

        [Fact]
        public async Task DisposingTheStoreStopsEveryTimerAndLeavesTheSessionsHeld()
        {
            FakeTimeProvider clock = Clock();
            InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, clock);
            ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);

            sessions.Dispose();
            clock.Advance(IdleTimeout * 2);

            Assert.Same(session, await sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Token));
            Assert.False(IsDisposed(session));
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-2", null, Token).AsTask());
        }

        [Fact]
        public async Task DisposingTheStoreWhileABuildIsInFlightAlsoStopsTheBuiltSessionsTimer()
        {
            FakeTimeProvider clock = Clock();
            using GatedSessionFactory factory = new(Factory());
            InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(factory), IdleTimeout, clock);

            Task<ConversationSession> opening = OwnThread.Run(
                () => sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token).AsTask(), Token);
            Assert.True(factory.Entered.Wait(TimeSpan.FromSeconds(30), Token), "the build never started.");

            sessions.Dispose();
            factory.Release();

            ConversationSession session = await opening;
            clock.Advance(IdleTimeout * 2);

            Assert.Same(session, await sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Token));
            Assert.False(IsDisposed(session));
        }

        [Fact]
        public void AnIdleTimeoutLongerThanATimerCanWaitIsRefused()
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(
                () => new InMemoryConversationSessions(SingleEntrySessionFactories.Of(Factory()), TimeSpan.FromDays(50), Clock()));
        }

        /// <summary>Reads whether the session was disposed: a disposed session refuses the turn slot.</summary>
        private static bool IsDisposed(ConversationSession session)
        {
            try
            {
                if (session.Cuts.TryEnterTurn())
                {
                    session.Cuts.ReleaseTurn();
                }

                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }
}

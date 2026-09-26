using AgentCore.Application.Runtime;
using AgentCore.Application.Sessions.Memory;
using AgentCore.TestSupport;
using Xunit;
using static AgentCore.Application.Tests.Sessions.ConversationSessionsFixture;

namespace AgentCore.Application.Tests.Sessions
{
    /// <summary>
    /// The boot sweep the one session owner runs once, at startup, over the workspace root: it deletes a
    /// marked folder a crashed process left behind while keeping one still inside the idle timeout, and never
    /// touches a folder without the marker, a folder this owner already holds, or a symbolic link out of the root.
    /// </summary>
    public sealed class InMemoryConversationSessionsSweepTests : IDisposable
    {
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

        private readonly string _root = Directory.CreateTempSubdirectory("agentcore-ws-sweep-").FullName;

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Fact]
        public void AFolderStampedTenMinutesAndOneSecondAgoIsDeleted()
        {
            FakeTimeProvider clock = Clock();
            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, clock);
            string folder = MarkedStampedFolder("stale", clock, TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

            sessions.SweepWorkspaceRoot(_root, logger: null);

            Assert.False(Directory.Exists(folder));
        }

        [Fact]
        public void AFolderStampedNineMinutesFiftyNineSecondsAgoIsKept()
        {
            FakeTimeProvider clock = Clock();
            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, clock);
            string folder = MarkedStampedFolder("fresh", clock, TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));

            sessions.SweepWorkspaceRoot(_root, logger: null);

            Assert.True(Directory.Exists(folder));
        }

        [Fact]
        public void DeletingAStaleFolderAlsoDeletesItsSiblingMarker()
        {
            FakeTimeProvider clock = Clock();
            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, clock);
            string folder = MarkedStampedFolder("stale", clock, TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

            sessions.SweepWorkspaceRoot(_root, logger: null);

            Assert.False(File.Exists(ConversationWorkspace.MarkerPathFor(folder)));
        }

        [Fact]
        public void AMarkerWhoseFolderIsStillFreshSurvivesEvenIfTheMarkersOwnStampIsOld()
        {
            // A marker is written once, at Create, and never touched again on reopen; a long-lived, actively
            // used folder's own stamp keeps moving while its marker's does not. The orphan-marker cleanup must
            // key off whether the folder exists, never off the marker's own age, or it would delete the marker
            // out from under a folder that is still very much alive.
            FakeTimeProvider clock = Clock();
            string folder = MarkedStampedFolder("alive", clock, TimeSpan.FromSeconds(1));
            File.SetLastWriteTimeUtc(
                ConversationWorkspace.MarkerPathFor(folder), clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(365));

            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, clock);
            sessions.SweepWorkspaceRoot(_root, logger: null);

            Assert.True(Directory.Exists(folder));
            Assert.True(File.Exists(ConversationWorkspace.MarkerPathFor(folder)));
        }

        [Fact]
        public void AMarkerWithNoFolderIsDeletedOnceItIsOldEnough()
        {
            // The folder it belonged to is gone — deleted out of band, say — but its marker survived. Nothing
            // stamps a marker on its own, so its age is frozen at creation; once that crosses the idle timeout
            // it is as stale as an ordinary folder would be.
            FakeTimeProvider clock = Clock();
            string marker = ConversationWorkspace.MarkerPathFor(Path.Combine(_root, "orphan"));
            File.WriteAllText(marker, string.Empty);
            File.SetLastWriteTimeUtc(marker, clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));

            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, clock);
            sessions.SweepWorkspaceRoot(_root, logger: null);

            Assert.False(File.Exists(marker));
        }

        [Fact]
        public void AMarkerWithNoFolderIsKeptWhileItIsStillFresh()
        {
            FakeTimeProvider clock = Clock();
            string marker = ConversationWorkspace.MarkerPathFor(Path.Combine(_root, "orphan"));
            File.WriteAllText(marker, string.Empty);
            File.SetLastWriteTimeUtc(marker, clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(9) - TimeSpan.FromSeconds(59));

            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, clock);
            sessions.SweepWorkspaceRoot(_root, logger: null);

            Assert.True(File.Exists(marker));
        }

        [Fact]
        public void AFolderWithoutTheMarkerIsKeptNoMatterHowOld()
        {
            // A workspace root pointed at a folder used for anything else — the current directory, a home
            // directory, a shared volume — must never lose a folder AgentCore did not make. Only a folder
            // ConversationWorkspace.Create wrote the marker into is ever a candidate for deletion.
            FakeTimeProvider clock = Clock();
            string unrelated = Directory.CreateDirectory(Path.Combine(_root, ".config")).FullName;
            File.WriteAllText(Path.Combine(unrelated, "settings.json"), "{}");
            Directory.SetLastWriteTimeUtc(unrelated, clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(365));

            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, clock);
            sessions.SweepWorkspaceRoot(_root, logger: null);

            Assert.True(Directory.Exists(unrelated));
            Assert.True(File.Exists(Path.Combine(unrelated, "settings.json")));
        }

        [Fact]
        public void NestedSymlinksInsideASweptFolderAreNotFollowed()
        {
            string outside = Directory.CreateTempSubdirectory("agentcore-ws-sweep-outside-").FullName;
            try
            {
                string precious = Path.Combine(outside, "precious.txt");
                File.WriteAllText(precious, "x");
                string outsideSub = Directory.CreateDirectory(Path.Combine(outside, "sub")).FullName;
                File.WriteAllText(Path.Combine(outsideSub, "deep.txt"), "y");

                FakeTimeProvider clock = Clock();
                string stale = Directory.CreateDirectory(Path.Combine(_root, "stale")).FullName;
                File.WriteAllText(ConversationWorkspace.MarkerPathFor(stale), string.Empty);
                string nested = Directory.CreateDirectory(Path.Combine(stale, "a", "b")).FullName;
                Directory.CreateSymbolicLink(Path.Combine(stale, "dirlink"), outside);
                Directory.CreateSymbolicLink(Path.Combine(nested, "deeplink"), outsideSub);
                File.CreateSymbolicLink(Path.Combine(stale, "filelink"), precious);
                Directory.SetLastWriteTimeUtc(stale, clock.GetUtcNow().UtcDateTime - TimeSpan.FromHours(1));

                using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, clock);
                sessions.SweepWorkspaceRoot(_root, logger: null);

                Assert.False(Directory.Exists(stale));
                Assert.True(File.Exists(precious));
                Assert.True(File.Exists(Path.Combine(outsideSub, "deep.txt")));
            }
            finally
            {
                Directory.Delete(outside, recursive: true);
            }
        }

        [Fact]
        public async Task AFolderThisOwnerAlreadyHoldsIsKeptRegardlessOfItsStamp()
        {
            FakeTimeProvider clock = Clock();
            using InMemoryConversationSessions sessions = new(
                SingleEntrySessionFactories.Of(Factory(workspaceRoot: _root, timeProvider: clock)), IdleTimeout, clock);

            _ = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, Token);
            string folder = Path.Combine(_root, "conversation-1");
            Directory.SetLastWriteTimeUtc(folder, clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(1));

            sessions.SweepWorkspaceRoot(_root, logger: null);

            Assert.True(Directory.Exists(folder));
        }

        [Fact]
        public async Task AHeldSessionBetweenTurnsSurvivesAnotherServersSweepAfterAShortTurn()
        {
            // A lookup between turns (the HTTP post-turn re-fetch, a voice keep-alive) restarts the idle clock;
            // it must restart the folder's stamp too, or another server sharing the root can sweep a session
            // that is still held here but has run no turn recently enough to have stamped it itself.
            FakeTimeProvider clock = Clock();
            ParkingConversationStore store = new();
            using InMemoryConversationSessions serverA = new(
                SingleEntrySessionFactories.Of(Factory(store, workspaceRoot: _root, timeProvider: clock)), IdleTimeout, clock);
            ConversationSession session = await serverA.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "c1", null, Token);

            Task turn = session.RunTurnAsync("hello", Token);
            await store.Parked;
            clock.Advance(TimeSpan.FromMinutes(5));
            store.Release();
            await turn;
            _ = await serverA.TryGetAsync(SingleEntrySessionFactories.MainEntry, "c1", Token);

            clock.Advance(TimeSpan.FromMinutes(7));
            Assert.Equal(1, serverA.Count);

            using InMemoryConversationSessions serverB = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, clock);
            serverB.SweepWorkspaceRoot(_root, logger: null);

            Assert.Equal(1, serverA.Count);
            Assert.True(Directory.Exists(session.Workspace!));
        }

        [Fact]
        public async Task ALongTurnWithNoBackgroundChildSurvivesASweepMidTurn()
        {
            // A turn can run for more than one idle timeout. The single stamp taken the moment it first crosses
            // the timeout is not enough on its own: a sweep that lands well after that moment, but still mid-turn,
            // must still see this folder as active.
            FakeTimeProvider clock = Clock();
            ParkingConversationStore store = new();
            using InMemoryConversationSessions serverA = new(
                SingleEntrySessionFactories.Of(Factory(store, workspaceRoot: _root, timeProvider: clock)), IdleTimeout, clock);
            ConversationSession session = await serverA.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "c1", null, Token);

            Task turn = session.RunTurnAsync("hello", Token);
            await store.Parked;
            clock.Advance(IdleTimeout);
            clock.Advance(IdleTimeout + TimeSpan.FromSeconds(1));

            using InMemoryConversationSessions serverB = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, clock);
            serverB.SweepWorkspaceRoot(_root, logger: null);

            Assert.False(turn.IsCompleted);
            Assert.True(Directory.Exists(session.Workspace!));

            store.Release();
            await turn;
        }

        [Fact]
        public async Task AReopenedOldFolderSurvivesASweepBeforeItsFirstTurn()
        {
            // Resuming a conversation reuses its old folder: CreateDirectory on a folder that already exists
            // does not touch its last-write time, so the reopen itself, not the first turn, must stamp it.
            FakeTimeProvider clock = Clock();
            string folder = Directory.CreateDirectory(Path.Combine(_root, "c1")).FullName;
            File.WriteAllText(ConversationWorkspace.MarkerPathFor(folder), string.Empty);
            File.WriteAllText(Path.Combine(folder, "notes.md"), "kept across unload?");
            Directory.SetLastWriteTimeUtc(folder, clock.GetUtcNow().UtcDateTime - TimeSpan.FromHours(1));

            using InMemoryConversationSessions serverA = new(
                SingleEntrySessionFactories.Of(Factory(workspaceRoot: _root, timeProvider: clock)), IdleTimeout, clock);
            _ = await serverA.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "c1", null, Token);

            using InMemoryConversationSessions serverB = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, clock);
            serverB.SweepWorkspaceRoot(_root, logger: null);

            Assert.Equal(1, serverA.Count);
            Assert.True(Directory.Exists(folder));
            Assert.True(File.Exists(Path.Combine(folder, "notes.md")));
        }

        [Fact]
        public void AFolderJustCreatedSurvivesEvenWhenTheHostClockRunsAheadOfTheWallClock()
        {
            // Create and the sweep both read the one TimeProvider a host gives them, never the real wall clock,
            // so a host clock that drifts ahead of real time never makes a brand-new folder look stale.
            FakeTimeProvider clock = new(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(11));
            ConversationWorkspace workspace = ConversationWorkspace.Create(_root, "c1", clock, logger: null);

            using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, clock);
            sessions.SweepWorkspaceRoot(_root, logger: null);

            Assert.True(Directory.Exists(workspace.Path));
        }

        [Fact]
        public void ASymbolicLinkUnderTheRootIsSkippedEvenWhenItsOwnStampIsOld()
        {
            // The link's own last-write time, not the target's: SetLastWriteTimeUtc on a symlink stamps the
            // link (proven against this runtime), so an old stamp here is what would make the sweep try to
            // delete it if the symlink skip were missing.
            string target = Directory.CreateTempSubdirectory("agentcore-ws-sweep-target-").FullName;
            try
            {
                string link = Path.Combine(_root, "linked");
                Directory.CreateSymbolicLink(link, target);
                File.WriteAllText(Path.Combine(target, "kept.txt"), "kept");

                FakeTimeProvider clock = Clock();
                Directory.SetLastWriteTimeUtc(link, clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(1));
                using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(Factory()), IdleTimeout, clock);

                sessions.SweepWorkspaceRoot(_root, logger: null);

                Assert.Equal(target, new DirectoryInfo(link).LinkTarget);
                Assert.True(File.Exists(Path.Combine(target, "kept.txt")));
            }
            finally
            {
                Directory.Delete(target, recursive: true);
            }
        }

        private string MarkedStampedFolder(string name, FakeTimeProvider clock, TimeSpan age)
        {
            string folder = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
            File.WriteAllText(ConversationWorkspace.MarkerPathFor(folder), string.Empty);
            Directory.SetLastWriteTimeUtc(folder, clock.GetUtcNow().UtcDateTime - age);
            return folder;
        }
    }
}

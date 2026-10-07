using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Tests.Transcript;
using AgentCore.TestSupport;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Sessions.ConversationSessionsFixture;

namespace AgentCore.Application.Tests.Sessions
{
    /// <summary>
    /// The workspace heartbeat (<see cref="HeldSession"/>'s re-arm at half the idle timeout while a turn runs
    /// past it) across more than one long turn on the same session: each turn gets its own run of stamps, the
    /// heartbeat resets cleanly between them, and the session still expires normally once both are done.
    /// </summary>
    public sealed class ConversationSessionWorkspaceHeartbeatTests : IDisposable
    {
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

        private readonly string _root = Directory.CreateTempSubdirectory("agentcore-ws-heartbeat-").FullName;

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private DateTime Stamp(string conversationId)
        {
            return Directory.GetLastWriteTimeUtc(Path.Combine(_root, conversationId));
        }

        [Fact]
        public async Task HeartbeatKeepsStampingAcrossTwoLongTurnsInTurnThenTheSessionExpires()
        {
            FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
            TaskCompletionSource[] gates = [new(), new()];
            TaskCompletionSource[] entered = [new(), new()];
            CapturingChatClient reply = new(async i => { _ = entered[i].TrySetResult(); await gates[i].Task; }, "a", "b");
            using InMemoryConversationSessions sessions = new(
                SingleEntrySessionFactories.Of(Factory(reply: reply, workspaceRoot: _root, timeProvider: clock)), IdleTimeout, clock);
            ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "c1", null, Token);

            for (int t = 0; t < 2; t++)
            {
                Task turn = session.RunTurnAsync("q" + t, Token);
                await entered[t].Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

                clock.Advance(IdleTimeout);
                Assert.Equal(clock.GetUtcNow().UtcDateTime, Stamp("c1"));

                // Six more half-idle-timeout ticks: three full idle timeouts of continuous heartbeat, well past
                // a single re-arm, so this proves the timer keeps re-arming itself rather than firing once.
                for (int tick = 0; tick < 6; tick++)
                {
                    clock.Advance(IdleTimeout / 2);
                    Assert.Equal(clock.GetUtcNow().UtcDateTime, Stamp("c1"));
                    Assert.Equal(1, sessions.Count);
                }

                // The release touch runs on a continuation after the turn frees its slot, not inside the turn,
                // so it is waited for by its own stamp: one the last heartbeat could not have written already.
                clock.Advance(TimeSpan.FromMinutes(1));
                DateTime freedAt = clock.GetUtcNow().UtcDateTime;
                gates[t].SetResult();
                await turn;
                await EventuallyAsync(() => Stamp("c1") == freedAt);
            }

            clock.Advance(IdleTimeout);
            await EventuallyAsync(() => sessions.Count == 0);
            await EventuallyAsync(() => !Directory.Exists(Path.Combine(_root, "c1")));
        }
    }
}

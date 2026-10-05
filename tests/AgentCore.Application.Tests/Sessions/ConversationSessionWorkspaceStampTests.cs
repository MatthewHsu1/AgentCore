using AgentCore.TestSupport;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Sessions.ConversationSessionsFixture;

namespace AgentCore.Application.Tests.Sessions
{
    /// <summary>
    /// A turn start stamps its conversation's workspace folder with the session's own clock, so the boot sweep
    /// (<see cref="InMemoryConversationSessionsSweepTests"/>) can tell a folder still in use from one a crashed
    /// process left behind.
    /// </summary>
    public sealed class ConversationSessionWorkspaceStampTests
    {
        [Fact]
        public async Task RunningATurnStampsTheWorkspaceFoldersLastWriteTimeToTheSessionsClock()
        {
            string root = Directory.CreateTempSubdirectory("agentcore-ws-turnstart-").FullName;
            try
            {
                FakeTimeProvider clock = Clock();
                ConversationSessionFactory factory = Factory(workspaceRoot: root, timeProvider: clock);
                await using ConversationSession session = factory.Create("conversation-1");

                clock.Advance(TimeSpan.FromMinutes(7));
                _ = await session.RunTurnAsync("hello", Token);

                Assert.Equal(clock.GetUtcNow().UtcDateTime, Directory.GetLastWriteTimeUtc(session.Workspace!));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

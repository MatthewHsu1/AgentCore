#pragma warning disable MAAI001 // The context types are evaluation-only in Microsoft.Agents.AI 1.21.0.

using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Harness
{
    /// <summary>
    /// A child session created inside the parent's run carries the parent's conversation id; one created
    /// outside any run carries nothing.
    /// </summary>
    public sealed class BackgroundChildAgentTests
    {
        [Fact]
        public async Task CreateSessionAsync_InsideTheParentsRun_StampsTheParentsConversationId()
        {
            // Arrange
            CancellationToken token = TestContext.Current.CancellationToken;
            BackgroundChildAgent child = new(new ChatClientAgent(new ScriptedChatClient("child")));
            SessionCreatingProvider creating = new(child);
            ChatClientAgent parent = new(new ScriptedChatClient("parent"), new ChatClientAgentOptions { AIContextProviders = [creating] });
            TurnInvocation turn = new() { ConversationId = "conversation-9", TurnIndex = 0, Stage = "s" };

            // Act
            _ = await parent.RunAsync("go", await parent.CreateSessionAsync(token), turn.RunOptions(), token);

            // Assert
            Assert.NotNull(creating.Created);
            Assert.True(creating.Created.StateBag.TryGetValue(BlobOwnerKey.Value, out string? stamped));
            Assert.Equal("conversation-9", stamped);
        }

        [Fact]
        public async Task CreateSessionAsync_InsideAParentRunWithAZone_StampsTheZoneOnTheChild()
        {
            // Arrange
            CancellationToken token = TestContext.Current.CancellationToken;
            BackgroundChildAgent child = new(new ChatClientAgent(new ScriptedChatClient("child")));
            SessionCreatingProvider creating = new(child);
            ChatClientAgent parent = new(new ScriptedChatClient("parent"), new ChatClientAgentOptions { AIContextProviders = [creating] });
            TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("Asia/Taipei", TimeSpan.FromHours(8), "Taipei", "Taipei");
            TurnInvocation turn = new() { ConversationId = "conversation-9", TurnIndex = 0, Stage = "s", TimeZone = zone };

            // Act
            _ = await parent.RunAsync("go", await parent.CreateSessionAsync(token), turn.RunOptions(), token);

            // Assert
            Assert.NotNull(creating.Created);
            Assert.True(creating.Created.StateBag.TryGetValue(CallerTimeZone.Key, out string? stamped));
            Assert.Equal("Asia/Taipei", stamped);
        }

        [Fact]
        public async Task CreateSessionAsync_InsideTheParentsRun_FilesTheParentsWorkspaceOnTheChild()
        {
            // Arrange
            CancellationToken token = TestContext.Current.CancellationToken;
            BackgroundChildAgent child = new(new ChatClientAgent(new ScriptedChatClient("child")));
            SessionCreatingProvider creating = new(child);
            ChatClientAgent parent = new(new ScriptedChatClient("parent"), new ChatClientAgentOptions { AIContextProviders = [creating] });
            TurnSources sources = new();
            TurnInvocation turn = new()
            {
                ConversationId = "conversation-9",
                TurnIndex = 0,
                Stage = "s",
                Workspace = "/work/conversation-9",
                Sources = sources,
            };

            // Act
            _ = await parent.RunAsync("go", await parent.CreateSessionAsync(token), turn.RunOptions(), token);

            // Assert
            Assert.NotNull(creating.Created);
            TurnInvocation? filed = TurnRegistry.For(creating.Created);
            Assert.NotNull(filed);
            Assert.Equal("/work/conversation-9", filed.Workspace);
            Assert.Equal("conversation-9", filed.ConversationId);
            Assert.True(filed.Nested);
            Assert.Null(filed.Sources);
        }

        [Fact]
        public async Task CreateSessionAsync_OutsideAnyRun_StampsNothing()
        {
            // Arrange
            BackgroundChildAgent child = new(new ChatClientAgent(new ScriptedChatClient("child")));

            // Act
            AgentSession session = await child.CreateSessionAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.False(session.StateBag.TryGetValue<string>(BlobOwnerKey.Value, out _));
        }

        private sealed class SessionCreatingProvider(AIAgent child) : AIContextProvider
        {
            public AgentSession? Created { get; private set; }

            protected override async ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken = default)
            {
                Created = await child.CreateSessionAsync(cancellationToken);
                return new AIContext();
            }
        }
    }
}

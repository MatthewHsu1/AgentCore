#pragma warning disable MAAI001 // The context types are evaluation-only in Microsoft.Agents.AI 1.21.0.

using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Harness;

/// <summary>
/// A child session created inside the parent's run carries the parent's call id; one created
/// outside any run carries nothing.
/// </summary>
/// <remarks>
/// <c>BackgroundAgentsProvider</c> creates the child session from inside the parent's tool call.
/// The test stands in for that with a provider that creates it from inside the parent's
/// <c>InvokingAsync</c>, which is the same run context.
/// </remarks>
public sealed class BackgroundChildAgentTests
{
    [Fact]
    public async Task CreateSessionAsync_InsideTheParentsRun_StampsTheParentsCallId()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        BackgroundChildAgent child = new(new ChatClientAgent(new ScriptedChatClient("child")));
        SessionCreatingProvider creating = new(child);
        ChatClientAgent parent = new(new ScriptedChatClient("parent"), new ChatClientAgentOptions { AIContextProviders = [creating] });
        var turn = new TurnInvocation { CallId = "call-9", TurnIndex = 0, Stage = "s" };

        // Act
        await parent.RunAsync("go", await parent.CreateSessionAsync(token), turn.RunOptions(), token);

        // Assert
        Assert.NotNull(creating.Created);
        Assert.True(creating.Created.StateBag.TryGetValue<string>(BlobOwnerKey.Value, out var stamped));
        Assert.Equal("call-9", stamped);
    }

    [Fact]
    public async Task CreateSessionAsync_InsideAParentRunWithAZone_StampsTheZoneOnTheChild()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        BackgroundChildAgent child = new(new ChatClientAgent(new ScriptedChatClient("child")));
        SessionCreatingProvider creating = new(child);
        ChatClientAgent parent = new(new ScriptedChatClient("parent"), new ChatClientAgentOptions { AIContextProviders = [creating] });
        var zone = TimeZoneInfo.CreateCustomTimeZone("Asia/Taipei", TimeSpan.FromHours(8), "Taipei", "Taipei");
        var turn = new TurnInvocation { CallId = "call-9", TurnIndex = 0, Stage = "s", TimeZone = zone };

        // Act
        await parent.RunAsync("go", await parent.CreateSessionAsync(token), turn.RunOptions(), token);

        // Assert
        Assert.NotNull(creating.Created);
        Assert.True(creating.Created.StateBag.TryGetValue<string>(CallerTimeZone.Key, out var stamped));
        Assert.Equal("Asia/Taipei", stamped);
    }

    [Fact]
    public async Task CreateSessionAsync_OutsideAnyRun_StampsNothing()
    {
        // Arrange
        BackgroundChildAgent child = new(new ChatClientAgent(new ScriptedChatClient("child")));

        // Act
        var session = await child.CreateSessionAsync(TestContext.Current.CancellationToken);

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

using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Calls;

/// <summary>The forwarding base every store fake is built on.</summary>
public sealed class DelegatingCallStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class CountingAppends(ICallStore inner) : DelegatingCallStore(inner)
    {
        public int Appends { get; private set; }

        public override ValueTask AppendAsync(
            IReadOnlyList<CallMessage> messages,
            CallSessionState? state = null,
            CancellationToken cancellationToken = default)
        {
            Appends++;
            return base.AppendAsync(messages, state, cancellationToken);
        }
    }

    [Fact]
    public async Task AnOverride_CountsItsOwnCalls_AndStillReachesTheInnerStore()
    {
        // Arrange
        CountingAppends store = new(new InMemoryCallStore());

        // Act
        await store.AppendAsync(
            [new CallMessage("c1", 0, 0, new ChatMessage(ChatRole.User, "hi"), "m0")], cancellationToken: Token);

        // Assert
        Assert.Equal(1, store.Appends);
        Assert.Single(await store.ReadAsync("c1", Token));
    }

    [Fact]
    public async Task AMethodNotOverridden_ReachesTheInnerStoreUnchanged()
    {
        // Arrange
        CountingAppends store = new(new InMemoryCallStore());

        // Act
        var call = await store.CreateAsync("c1", Token);

        // Assert
        Assert.Equal("c1", call.CallId);
        Assert.Equal(0, store.Appends);
    }
}

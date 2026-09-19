using AgentCore.Application.Ports;
using AgentCore.Application.Conversation;
using Xunit;

namespace AgentCore.Application.Tests.Conversation;

/// <summary>
/// The channel pair, including the case where one object fills both slots.
/// </summary>
public sealed class ConversationChannelTests
{
    [Fact]
    public async Task OneObjectInBothSlots_IsDisposedExactlyOnce()
    {
        var both = new CountingChannel();
        var channel = new ConversationChannel(both, both);

        await channel.DisposeAsync();

        Assert.Equal(1, both.Disposals);
    }

    [Fact]
    public async Task TwoDistinctObjects_AreEachDisposedOnce()
    {
        var input = new CountingChannel();
        var output = new CountingChannel();
        var channel = new ConversationChannel(input, output);

        await channel.DisposeAsync();

        Assert.Equal(1, input.Disposals);
        Assert.Equal(1, output.Disposals);
    }

    [Fact]
    public async Task DisposingTwice_DisposesTheInnerPortsOnlyOnce()
    {
        var both = new CountingChannel();
        var channel = new ConversationChannel(both, both);

        await channel.DisposeAsync();
        await channel.DisposeAsync();

        Assert.Equal(1, both.Disposals);
    }

    /// <summary>A port that fills either slot, or both, and counts its own disposals.</summary>
    private sealed class CountingChannel : IConversationInputPort, IConversationOutputPort
    {
        public int Disposals { get; private set; }

        public IAsyncEnumerable<ConversationInput> ListenAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask SpeakAsync(string fragment, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}

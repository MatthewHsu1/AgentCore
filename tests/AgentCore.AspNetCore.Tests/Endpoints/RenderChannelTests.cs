using AgentCore.Application.Ports;
using AgentCore.AspNetCore.Endpoints;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints;

/// <summary>
/// The queue between a tool that draws and the stream that writes what it drew.
/// </summary>
/// <remarks>
/// The split matters: the payload goes to the browser, and the model is told what was drawn in
/// words. What the model is told is what the transcript and the audit record keep, so a payload
/// that leaked into the receipt would be kept forever as if it were speech.
/// </remarks>
public sealed class RenderChannelTests
{
    [Fact]
    public void TheChannel_IsTheScreenPortAToolFinds()
    {
        Assert.IsAssignableFrom<IRenderPort>(new RenderChannel());
    }

    [Fact]
    public void TakingPayloads_GivesEachOnceAndInTheOrderItWasPublished()
    {
        RenderChannel channel = new();
        channel.Publish("chart", new { title = "first" });
        channel.Publish("card", new { title = "second" });

        Assert.True(channel.TryTake(out var first));
        Assert.Equal("chart", first.Name);
        Assert.True(channel.TryTake(out var second));
        Assert.Equal("card", second.Name);
        Assert.False(channel.TryTake(out _));
    }

    [Fact]
    public void AnIdleChannel_TakesNothingRatherThanThrowing()
    {
        // The streaming path drains on every update, and most updates have drawn nothing at all.
        Assert.False(new RenderChannel().TryTake(out _));
    }

    [Fact]
    public void AWriteThatThrowsPartWayThrough_LeavesTheRestOfTheQueueIntact()
    {
        // The bug this pins: taking the whole queue before writing any of it loses every payload
        // after the one whose write throws, while the model has already been told the caller can
        // see them all. One at a time, a disconnect costs only the payload being written.
        RenderChannel channel = new();
        channel.Publish("first", new { });
        channel.Publish("second", new { });
        channel.Publish("third", new { });

        List<string> written = [];

        Assert.Throws<IOException>(() =>
        {
            while (channel.TryTake(out var payload))
            {
                if (payload.Name == "second")
                {
                    throw new IOException("the caller went away");
                }

                written.Add(payload.Name);
            }
        });

        Assert.Equal(["first"], written);
        Assert.True(channel.TryTake(out var survivor));
        Assert.Equal("third", survivor.Name);
    }

    [Fact]
    public void Clearing_DropsEverythingSoNothingAppearsPartWayThroughALaterTurn()
    {
        RenderChannel channel = new();
        channel.Publish("chart", new { });

        channel.Clear();

        Assert.False(channel.TryTake(out _));
    }
}

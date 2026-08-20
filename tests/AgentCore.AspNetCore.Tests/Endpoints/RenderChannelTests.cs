using AgentCore.AspNetCore.Endpoints;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints;

/// <summary>
/// The queue between a tool that draws and the stream that writes what it drew.
/// </summary>
/// <remarks>
/// The split matters: the model is told only that the caller can see the thing, and the payload
/// itself goes to the browser. What the model is told is what the transcript and the audit record
/// keep, so a payload that leaked into the receipt would be kept forever as if it were speech.
/// </remarks>
public sealed class RenderChannelTests
{
    [Fact]
    public void APublishedPayload_IsAnsweredWithAReceiptThatCarriesNoneOfIt()
    {
        RenderChannel channel = new();

        int[] points = [1, 2, 3];
        var receipt = channel.Publish("chart", new { title = "Q3", points });

        Assert.DoesNotContain("points", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("Q3", receipt, StringComparison.Ordinal);
        Assert.Contains("chart", receipt, StringComparison.Ordinal);
    }

    [Fact]
    public void ADrain_TakesEachPayloadOnceAndInTheOrderItWasPublished()
    {
        RenderChannel channel = new();
        channel.Publish("chart", new { title = "first" });
        channel.Publish("card", new { title = "second" });

        var taken = channel.Drain();

        Assert.Equal(["chart", "card"], taken.Select(payload => payload.Name));
        Assert.Empty(channel.Drain());
    }

    [Fact]
    public void ADrainOfAnIdleChannel_IsEmptyRatherThanNull()
    {
        // The streaming path drains on every update, and most updates have drawn nothing at all.
        Assert.Empty(new RenderChannel().Drain());
    }
}

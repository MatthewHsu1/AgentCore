using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Transcript;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Turn;

/// <summary>
/// What a turn has published and not yet attached to a message. Keyed by the outer tool call, like
/// the render and source collectors, because the message a file card belongs on is the result of
/// the call that published it.
/// </summary>
public sealed class TurnFilesTests
{
    [Fact]
    public void Publish_UnderACall_IsTakenByThatCallOnce()
    {
        TurnFiles files = new();

        using (files.BeginOuterCall("call-1"))
        {
            files.Publish(Card("rows.csv", 8));
        }

        var taken = files.TakeFor("call-1");

        Assert.Equal("rows.csv", Assert.Single(taken).Name);
        Assert.Empty(files.TakeFor("call-1"));
    }

    [Fact]
    public void Publish_WithNoCallOpen_IsDropped()
    {
        TurnFiles files = new();

        files.Publish(Card("rows.csv", 8));

        Assert.Empty(files.TakeFor("rows.csv"));
    }

    [Fact]
    public void Publish_TheSameNameTwice_KeepsTheLaterFactsInTheFirstPlace()
    {
        TurnFiles files = new();

        using (files.BeginOuterCall("call-1"))
        {
            files.Publish(Card("rows.csv", 8));
            files.Publish(Card("chart.png", 3));
            files.Publish(Card("rows.csv", 16));
        }

        var taken = files.TakeFor("call-1");

        Assert.Equal(["rows.csv", "chart.png"], taken.Select(card => card.Name));
        Assert.Equal(16, taken[0].Length);
    }

    [Fact]
    public void Publish_InsideANestedCall_FilesUnderTheOuterCall()
    {
        TurnFiles files = new();

        using (files.BeginOuterCall("outer"))
        {
            using (files.BeginOuterCall("inner"))
            {
                files.Publish(Card("rows.csv", 8));
            }

            files.Publish(Card("chart.png", 3));
        }

        Assert.Single(files.TakeFor("inner"));
        Assert.Single(files.TakeFor("outer"));
    }

    private static FileContent Card(string name, long length)
        => new() { Name = name, FileId = name, MediaType = "text/csv", Length = length, Kept = true };
}

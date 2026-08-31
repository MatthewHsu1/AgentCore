using AgentCore.Application.Runtime;
using AgentCore.Domain.Sources;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// What a turn has cited and not yet attached to a message.
/// </summary>
/// <remarks>
/// Keyed by the outer tool call for the same reason the render collector is: the message a source
/// belongs on is the result of the call that produced it, and a nested call must not steal it.
/// </remarks>
public sealed class TurnSourcesTests
{
    [Fact]
    public void Publish_UnderACall_IsTakenByThatCall()
    {
        TurnSources sources = new();

        using (OpenCall("call-1"))
        {
            sources.Publish(Reference("card-1"));
        }

        var taken = sources.TakeFor("call-1");

        var content = Assert.Single(taken);
        Assert.Equal("card-1", content.Source.SourceId);
    }

    [Fact]
    public void Publish_WithNoCallOpen_IsDropped()
    {
        // mode: prefetch searches before any tool call exists. There is no message to attach to, so
        // the publish is dropped rather than attached to whatever message comes next.
        TurnSources sources = new();

        sources.Publish(Reference("card-1"));

        Assert.Empty(sources.TakeFor("call-1"));
    }

    [Fact]
    public void Publish_TheSameIdTwice_IsShownOnce()
    {
        // One turn may search twice and both searches may return the same card. Two identical chips
        // are noise, and the second publish is the fresher one.
        TurnSources sources = new();

        using (OpenCall("call-1"))
        {
            sources.Publish(Reference("card-1") with { Title = "first" });
            sources.Publish(Reference("card-1") with { Title = "second" });
        }

        var content = Assert.Single(sources.TakeFor("call-1"));
        Assert.Equal("second", content.Source.Title);
    }

    [Fact]
    public void TakeFor_TakesOnlyOnce()
    {
        TurnSources sources = new();

        using (OpenCall("call-1"))
        {
            sources.Publish(Reference("card-1"));
        }

        Assert.Single(sources.TakeFor("call-1"));
        Assert.Empty(sources.TakeFor("call-1"));
    }

    [Fact]
    public void Publish_UnderTwoDifferentCalls_StampsEachSourceWithItsOwnCallId()
    {
        // A round can hold two parallel tool calls, and the base client batches both results onto
        // one message — so the source has to carry its own call id rather than being matched to
        // whichever call happens to be findable on that shared message afterward.
        TurnSources sources = new();

        using (OpenCall("call-1"))
        {
            sources.Publish(Reference("card-1"));
        }

        using (OpenCall("call-2"))
        {
            sources.Publish(Reference("card-2"));
        }

        var fromCallOne = Assert.Single(sources.TakeFor("call-1"));
        Assert.Equal("call-1", fromCallOne.CallId);
        Assert.Equal("card-1", fromCallOne.Source.SourceId);

        var fromCallTwo = Assert.Single(sources.TakeFor("call-2"));
        Assert.Equal("call-2", fromCallTwo.CallId);
        Assert.Equal("card-2", fromCallTwo.Source.SourceId);
    }

    [Fact]
    public void CallSourceScope_IsWhatAProducerPublishesThrough()
    {
        TurnSources sources = new();

        Assert.Null(CallSourceScope.Current);

        using (TurnAmbientsTestScope.WithSources(sources))
        using (OpenCall("call-1"))
        {
            Assert.NotNull(CallSourceScope.Current);
            CallSourceScope.Current!.Publish(Reference("card-1"));
        }

        Assert.Single(sources.TakeFor("call-1"));
    }

    private static IDisposable OpenCall(string callId) => TurnAmbientsTestScope.WithOuterCall(callId);

    private static SourceReference Reference(string id) => new()
    {
        SourceId = id,
        Kind = SourceKind.Document,
        Title = "a title",
        Origin = "knowledge",
    };
}

using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Transcript;
using AgentCore.Domain.Sources;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Turn
{
    /// <summary>
    /// What a turn has cited and not yet attached to a message.
    /// </summary>
    public sealed class TurnSourcesTests
    {
        [Fact]
        public void Publish_UnderACall_IsTakenByThatCall()
        {
            TurnSources sources = new();

            sources.Publish(Reference("card-1"), "call-1");

            IReadOnlyList<SourceContent> taken = sources.TakeFor("call-1");

            SourceContent content = Assert.Single(taken);
            Assert.Equal("card-1", content.Source.SourceId);
        }

        [Fact]
        public void Publish_OutsideAnyCall_IsDropped()
        {
            // mode: prefetch searches before any tool call exists. There is no message to attach to, so
            // the publish is dropped rather than attached to whatever message comes next.
            TurnSources sources = new();

            sources.Publish(Reference("card-1"), callId: null);

            Assert.Empty(sources.TakeFor("call-1"));
        }

        [Fact]
        public void Publish_TheSameIdTwice_IsShownOnce()
        {
            // One turn may search twice and both searches may return the same card. Two identical chips
            // are noise, and the second publish is the fresher one.
            TurnSources sources = new();

            sources.Publish(Reference("card-1") with { Title = "first" }, "call-1");
            sources.Publish(Reference("card-1") with { Title = "second" }, "call-1");

            SourceContent content = Assert.Single(sources.TakeFor("call-1"));
            Assert.Equal("second", content.Source.Title);
        }

        [Fact]
        public void TakeFor_TakesOnlyOnce()
        {
            TurnSources sources = new();

            sources.Publish(Reference("card-1"), "call-1");

            _ = Assert.Single(sources.TakeFor("call-1"));
            Assert.Empty(sources.TakeFor("call-1"));
        }

        [Fact]
        public void Publish_UnderTwoDifferentCalls_StampsEachSourceWithItsOwnCallId()
        {
            // A round can hold two parallel tool calls, and the base client batches both results onto
            // one message — so the source has to carry its own call id rather than being matched to
            // whichever call happens to be findable on that shared message afterward.
            TurnSources sources = new();

            sources.Publish(Reference("card-1"), "call-1");

            sources.Publish(Reference("card-2"), "call-2");

            SourceContent fromCallOne = Assert.Single(sources.TakeFor("call-1"));
            Assert.Equal("call-1", fromCallOne.CallId);
            Assert.Equal("card-1", fromCallOne.Source.SourceId);

            SourceContent fromCallTwo = Assert.Single(sources.TakeFor("call-2"));
            Assert.Equal("call-2", fromCallTwo.CallId);
            Assert.Equal("card-2", fromCallTwo.Source.SourceId);
        }

        private static SourceReference Reference(string id)
        {
            return new()
            {
                SourceId = id,
                Kind = SourceKind.Document,
                Title = "a title",
                Origin = "knowledge",
            };
        }
    }
}

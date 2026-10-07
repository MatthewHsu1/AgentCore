using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>The sideband reader names what it understands and turns every other frame into an event; it never throws.</summary>
    public sealed class LiveEventReaderTests
    {
        private static readonly Dictionary<string, LiveEvent> Expected = new()
        {
            ["not json at all"] = new LiveEvent.Other("unparseable"),
            ["""{"delta":"hi"}"""] = new LiveEvent.Other(string.Empty),
            ["""{"type":"session.input_transcript.delta","delta":"hi","start_ms":0}"""] = new LiveEvent.Other("session.input_transcript.delta"),
            ["""{"type":"session.delegation.created","delegation":{},"offset_ms":10}"""] = new LiveEvent.Other("session.delegation.created"),
            ["""{"type":"error"}"""] = new LiveEvent.Failed(null, null),
            ["[1,2]"] = new LiveEvent.Other(string.Empty),
            ["""{"type":"session.input_transcript.delta","delta":"hi","start_ms":1200.5,"end_ms":1800.0}"""] = new LiveEvent.Transcript(Speaker.Caller, "hi", 1200, 1800),
        };

        public static TheoryData<string> UnusableFrames => [.. Expected.Keys];

        [Fact]
        public void TheReaderNamesAnUnmodelledEventAndAnError()
        {
            Assert.Equal(new LiveEvent.Other("session.usage.updated"), LiveEventReader.Read("""{"type":"session.usage.updated","usage":{"seconds":14.0}}"""));
            Assert.Equal(new LiveEvent.Failed("bad_delegation", "no such delegation"), LiveEventReader.Read("""{"type":"error","error":{"code":"bad_delegation","message":"no such delegation"}}"""));
        }

        // The ack names the append it acknowledges by our event_id, echoed as client_event_id.
        [Fact]
        public void TheReaderNamesTheAppendACommentaryAckAcknowledges()
        {
            IReadOnlyList<string> log = LiveLog.Inbound("p1-a");

            Assert.Equal(new LiveEvent.Appended("c3"), LiveEventReader.Read(log[LiveLog.IndexOf(log, "session.commentary.appended")]));
        }

        // The read loop hangs up on a throw, so a frame it cannot use must come back as an event, never an exception.
        [Theory]
        [MemberData(nameof(UnusableFrames))]
        public void TheReaderTurnsAFrameItCannotUseIntoAnEvent(string json)
        {
            Assert.Equal(Expected[json], LiveEventReader.Read(json));
        }
    }
}

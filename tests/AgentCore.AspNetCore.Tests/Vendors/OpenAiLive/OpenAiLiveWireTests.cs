using System.Text;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Webhook;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>
    /// The incoming-call webhook as <see cref="OpenAiLiveWire"/> assumes it (the OpenAI SIP guide's shape). These tests
    /// pin the parser to that shape, so a correction shows up as a failing test.
    /// </summary>
    public sealed class OpenAiLiveWireTests
    {
        internal const string IncomingCall = """
        {"object":"event","id":"evt_1","type":"realtime.call.incoming","created_at":1790500000,
         "data":{"call_id":"rtc_123","sip_headers":[
           {"name":"From","value":"\"Caller\" <sip:+15550100@sip.telnyx.com>;tag=1"},
           {"name":"To","value":"<sip:+15550199@sip.api.openai.com>"},
           {"name":"Diversion","value":"<sip:+15550111@goto.example>;reason=unconditional"}]}}
        """;

        [Fact]
        public void AnIncomingCallNamesItsIdTheTwoNumbersAndEveryHeader()
        {
            LiveIncomingCall call = OpenAiLiveWire.ReadIncomingCall(Encoding.UTF8.GetBytes(IncomingCall))!;

            Assert.Equal(("rtc_123", "+15550100", "+15550199"), (call.CallId, call.From, call.To));
            Assert.Equal("<sip:+15550111@goto.example>;reason=unconditional", call.Headers["diversion"]);
        }

        // The GPT-Live docs name live.transport.incoming, the SIP guide realtime.call.incoming.
        [Fact]
        public void TheOtherDocumentedEventNameAndASessionIdAreReadToo()
        {
            const string body = """{"type":"live.transport.incoming","data":{"session_id":"live_9","sip_headers":[]}}""";

            Assert.Equal("live_9", OpenAiLiveWire.ReadIncomingCall(Encoding.UTF8.GetBytes(body))!.CallId);
        }

        [Fact]
        public void AnotherEventIsNotACall()
        {
            Assert.Null(OpenAiLiveWire.ReadIncomingCall(Encoding.UTF8.GetBytes("""{"type":"realtime.call.ended","data":{"call_id":"rtc_123"}}""")));
        }

        // The OpenAI SIP guide: POST /v1/live/sessions/{session_id}/refer with { "target_uri": "sip:agent@example.com" }.
        [Fact]
        public void AReferGoesToTheSessionsReferPathWithTheTargetUri()
        {
            Assert.Equal("v1/live/sessions/live_9/refer", OpenAiLiveWire.ReferPath("live_9"));
            Assert.Equal("""{"target_uri":"sip:agent@example.com"}""", OpenAiLiveWire.ReferBody(new Uri("sip:agent@example.com")).ToJsonString());
        }

        [Theory]
        [InlineData("\"Caller\" <sip:+15550100@sip.telnyx.com>;tag=1", "+15550100")]
        [InlineData("<tel:+15550100>", "+15550100")]
        [InlineData("sip:anonymous@anonymous.invalid", "anonymous")]
        [InlineData("garbage", null)]
        public void TheUserPartOfASipAddressIsTheNumber(string header, string? number)
        {
            Assert.Equal(number, OpenAiLiveWire.SipUser(header));
        }
    }
}

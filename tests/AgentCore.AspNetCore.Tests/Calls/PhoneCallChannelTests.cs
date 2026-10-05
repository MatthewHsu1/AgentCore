using AgentCore.Application.Conversation.Actions;
using AgentCore.Application.Runtime.Session;
using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Tests.Fakes;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Calls
{
    public sealed class PhoneCallChannelTests
    {
        private static readonly TransferAction Transfer = new(new Uri("tel:+15550002222"));

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // The tools of the next ask run on the session reopened after an idle unload, so the vendor's actions must be there.
        [Fact]
        public async Task TheChannelFollowsTheCallToASessionReopenedAfterAnUnload()
        {
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), []);
            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(), Ct)).Call!;
            await call.StartAsync(Ct);
            call.Channel.Use(new AcceptingChannel(), call.Session);
            Assert.Equal(ConversationActionResult.Scheduled, call.Session.Request(Transfer));
            await harness.Sessions.CloseAsync("main", "call-7", Ct);

            ConversationSession reopened = await call.LiveSessionAsync(Ct);

            Assert.Equal(ConversationActionResult.Scheduled, reopened.Request(Transfer));
        }

        private sealed class AcceptingChannel : IConversationChannel
        {
            public ConversationActionResult Request(ConversationAction action) => ConversationActionResult.Scheduled;
        }
    }
}

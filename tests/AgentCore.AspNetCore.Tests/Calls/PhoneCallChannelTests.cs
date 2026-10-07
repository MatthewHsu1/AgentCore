using AgentCore.Application.Conversation.Commands;
using AgentCore.Application.Runtime.Session;
using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Tests.Fakes;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Calls
{
    public sealed class PhoneCallChannelTests
    {
        private static readonly TransferCommand Transfer = new(new Uri("tel:+15550002222"));

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // The tools of the next ask run on the session reopened after an idle unload, so the vendor's channel must be there.
        [Fact]
        public async Task TheChannelFollowsTheCallToASessionReopenedAfterAnUnload()
        {
            using PhoneCallHarness harness = PhoneCallHarness.Create(new StallOnCueChatClient(), []);
            PhoneCall call = (await PhoneCall.AdmitAsync(harness.Host, PhoneCallHarness.Offer(), Ct)).Call!;
            await call.StartAsync(Ct);
            call.Channel.Use(new AcceptingChannel(), call.Session);
            Assert.Equal(ChannelCommandResult.Scheduled, call.Session.Send(Transfer));
            await harness.Sessions.CloseAsync("main", "call-7", Ct);

            ConversationSession reopened = await call.LiveSessionAsync(Ct);

            Assert.Equal(ChannelCommandResult.Scheduled, reopened.Send(Transfer));
        }

        private sealed class AcceptingChannel : IConversationChannel
        {
            public ChannelCommandResult Send(ChannelCommand command)
            {
                return ChannelCommandResult.Scheduled;
            }
        }
    }
}

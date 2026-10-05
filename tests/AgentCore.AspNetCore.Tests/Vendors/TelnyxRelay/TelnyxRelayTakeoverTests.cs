using System.Net.WebSockets;
using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay
{
    /// <summary>
    /// LiveKit's duplicate-identity rule on the relay: the newest
    /// connection for a call id holds the call, the older one is dropped with a reason, and the conversation goes on.
    /// </summary>
    public sealed class TelnyxRelayTakeoverTests
    {
        private const string ReplacedDescription = "replaced by a newer connection for the same call";

        [Fact(Timeout = 30_000)]
        public async Task ASecondSocketForTheSameCallTakesItOverAndTheFirstIsClosedAsReplaced()
        {
            RecordingHook hook = new();
            using FragmentingChatClient reply = new("hello");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(TelnyxRelayTurnTests.PolicyYaml, reply, options => options.UseHooks(hook));
            await using FakeRelayClient first = await host.ConnectAsync();
            await first.SendAsync(RelayFrames.Setup());
            _ = await hook.WaitForAsync<CallStarted>();
            ConversationSession before = (await host.FindSessionAsync("conversation-one"))!;

            await using FakeRelayClient second = await host.ConnectAsync();
            await second.SendAsync(RelayFrames.Setup());
            (WebSocketCloseStatus? Status, string? Description) closed = await first.ReadCloseAsync();
            await second.SendAsync(RelayFrames.Prompt("hi", last: true));
            string spoken = string.Concat(await second.ReadTextFramesUntilLastAsync());
            await before.FlushNoticesAsync();

            Assert.Equal(((WebSocketCloseStatus?)WebSocketCloseStatus.NormalClosure, (string?)ReplacedDescription), closed);
            Assert.Equal("hello", spoken);
            Assert.Same(before, await host.FindSessionAsync("conversation-one"));
            Assert.Empty(hook.Of<ConversationEnded>());
        }

        // The newest setup frame for the same call id takes the call over on the same socket, which stays up.
        [Fact(Timeout = 30_000)]
        public async Task ASecondSetupFrameForTheSameCallKeepsItsConversationGoing()
        {
            RecordingHook hook = new();
            using FragmentingChatClient reply = new("hello");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(TelnyxRelayTurnTests.PolicyYaml, reply, options => options.UseHooks(hook));
            await using FakeRelayClient relay = await host.ConnectAsync();
            await relay.SendAsync(RelayFrames.Setup());
            _ = await hook.WaitForAsync<CallStarted>();
            ConversationSession before = (await host.FindSessionAsync("conversation-one"))!;

            await relay.SendAsync(RelayFrames.Setup());
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            string spoken = string.Concat(await relay.ReadTextFramesUntilLastAsync());
            await before.FlushNoticesAsync();

            Assert.Equal("hello", spoken);
            Assert.Same(before, await host.FindSessionAsync("conversation-one"));
            Assert.Empty(hook.Of<ConversationEnded>());
            _ = Assert.Single(hook.Of<CallStarted>());
        }
    }
}

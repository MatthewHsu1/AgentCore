using System.Net;
using AgentCore.Application.Secrets;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay;
using AgentCore.AspNetCore.Vendors.TelnyxRelay;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Conversation
{
    /// <summary>
    /// The one place the shipped vendor and the vendor-neutral route meet in a running host.
    /// </summary>
    public sealed class TelnyxRelayThroughTheConversationSeamTests
    {
        [Fact(Timeout = 30_000)]
        public async Task APlainGetToTheVendorNeutralRoute_ReachesTheRelayHandler()
        {
            using FragmentingChatClient reply = new("hello");

            // The host names no vendor at the route. providers.conversation: { kind: telnyx-relay } is what
            // picks this adapter, and app.MapCall() is what asks.
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartThroughConversationSeamAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                options => options.UseConversation(TelnyxRelayHost.KeyedAdapter()));

            HttpResponseMessage answer = await host.GetAsync(TelnyxRelayHost.KeyedMainConversation);

            // 404 would mean MapCall mapped nothing, and every call to this deployment would be lost
            // with nothing to read. 400 is the relay's own HandleAsync refusing a request that is not a
            // WebSocket upgrade, so it proves both halves at once: the route exists, and the handler
            // behind it is the Telnyx one.
            Assert.NotEqual(HttpStatusCode.NotFound, answer.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        }

        // Telnyx signs no relay socket, so the key in its URL is the only proof a caller is Telnyx.
        [Theory(Timeout = 30_000)]
        [InlineData(TelnyxRelayHost.MainConversation)]
        [InlineData(TelnyxRelayHost.MainConversation + "?key=wrong")]
        [InlineData(TelnyxRelayHost.MainConversation + "?key=relay-key-")]
        public async Task ACallerWithoutTheRelayKeyIsRefusedBeforeTheRelayOrTheEntryGate(string route)
        {
            using FragmentingChatClient reply = new("hello");
            EntryCounter entries = new();
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartThroughConversationSeamAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                options => options.UseConversation(TelnyxRelayHost.KeyedAdapter()).UseHooks(entries));

            HttpResponseMessage answer = await host.GetAsync(route);

            Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
            Assert.Equal(0, entries.Count);
        }

        // A relay route with no key to check would be open to anyone, so the host does not start.
        [Fact(Timeout = 30_000)]
        public async Task AHostWhoseRelayHasNoKeyDoesNotStart()
        {
            using FragmentingChatClient reply = new("hello");

            _ = await Assert.ThrowsAsync<SecretResolutionException>(() => TelnyxRelayHost.StartThroughConversationSeamAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                options => options.UseConversation(new TelnyxRelayConversationAdapter(() => new MapSecretResolver()))));
        }
    }
}

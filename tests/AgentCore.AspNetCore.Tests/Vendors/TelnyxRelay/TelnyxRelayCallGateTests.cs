using System.Net.WebSockets;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Vendors.TelnyxRelay;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay
{
    /// <summary>A relay call goes through the call gate, the call notices, and the brief.</summary>
    public sealed class TelnyxRelayCallGateTests
    {
        private static readonly string StoreYaml = TelnyxRelayTurnTests.PolicyYaml.Replace("  llm:\n", "  conversations: { kind: test }\n  llm:\n", StringComparison.Ordinal);

        // With no hook, the conversation id is unchanged.
        [Fact(Timeout = 30_000)]
        public async Task WithNoHookARelayCallKeepsItsConversationIdAndStartsAsACall()
        {
            RecordingHook hook = new();
            using FragmentingChatClient reply = new("hello");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(TelnyxRelayTurnTests.PolicyYaml, reply, options => options.UseHooks(hook));
            await using FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup(conversationSessionId: "logical-7"));
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            _ = await relay.ReadTextFramesUntilLastAsync();

            Assert.NotNull(await host.FindSessionAsync("logical-7"));
            CallStarted started = await hook.WaitForAsync<CallStarted>();
            Assert.Equal(("logical-7", "+13122010094", "+13122123456", "telnyx-relay"), (started.CallId, started.From, started.To, started.Transport));
        }

        [Fact(Timeout = 30_000)]
        public async Task AHookThatDeclinesTheCallClosesTheSocketAndOpensNothing()
        {
            using FragmentingChatClient reply = new("hello");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml, reply, options => options.UseHooks(new CallDecider(gate => gate.Reject(CallRefusal.Declined))));
            await using FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup());

            Assert.Equal(((WebSocketCloseStatus?)WebSocketCloseStatus.PolicyViolation, (string?)"the call was declined"), await relay.ReadCloseAsync());
            Assert.Null(await host.FindSessionAsync("conversation-one"));
        }

        // A refusal other than busy or declined names the call as one that could not be taken now.
        [Fact(Timeout = 30_000)]
        public async Task AHookThatRejectsTheCallAsUnavailableClosesTheSocketSayingSo()
        {
            using FragmentingChatClient reply = new("hello");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml, reply, options => options.UseHooks(new CallDecider(gate => gate.Reject(CallRefusal.Unavailable))));
            await using FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup());

            Assert.Equal(((WebSocketCloseStatus?)WebSocketCloseStatus.PolicyViolation, (string?)"the call could not be taken now"), await relay.ReadCloseAsync());
            Assert.Null(await host.FindSessionAsync("conversation-one"));
        }

        [Fact(Timeout = 30_000)]
        public async Task AHookThatAcceptsUnderItsOwnIdRunsTheConversationThere()
        {
            using FragmentingChatClient reply = new("hello");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml, reply, options => options.UseHooks(new CallDecider(gate => gate.Accept("thread-1"))));
            await using FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup());
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            _ = await relay.ReadTextFramesUntilLastAsync();

            Assert.NotNull(await host.FindSessionAsync("thread-1"));
            Assert.Null(await host.FindSessionAsync("conversation-one"));
        }

        [Fact(Timeout = 30_000)]
        public async Task AHangUpEndsTheCallWithItsIdAndNoCause()
        {
            RecordingHook hook = new();
            using FragmentingChatClient reply = new("hello");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(TelnyxRelayTurnTests.PolicyYaml, reply, options => options.UseHooks(hook));
            FakeRelayClient relay = await host.ConnectAsync();
            await relay.SendAsync(RelayFrames.Setup());
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            _ = await relay.ReadTextFramesUntilLastAsync();

            await relay.DisposeAsync();
            ConversationEnded ended = await hook.WaitForAsync<ConversationEnded>();

            Assert.Equal(ConversationEndReason.CallerHungUp, ended.Reason);
            Assert.Equal(("conversation-one", (string?)null), (ended.Call!.CallId, ended.Call.Cause));
        }

        // A relay call's brief reaches the engine too.
        [Fact(Timeout = 30_000)]
        public async Task TheBriefOfARelayCallReachesTheEngine()
        {
            using FragmentingChatClient inner = new("hello");
            InstructionsSeenChatClient reply = new(inner);
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartThroughConversationSeamAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                options => options.UseConversation(TelnyxRelayHost.KeyedAdapter()).UseHooks(new CallDecider(gate => gate.Accept("thread-2", "BRIEF-X"))));
            await using FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup());
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            _ = await relay.ReadTextFramesUntilLastAsync();

            Assert.Contains("BRIEF-X", reply.Instructions[0], StringComparison.Ordinal);
        }

        // Two live calls never share a conversation. A second socket for another call on the same conversation is
        // refused, and the first goes on.
        [Fact(Timeout = 30_000)]
        public async Task ASecondSocketForAnotherCallOnTheSameConversationIsRefusedAsHeldByAnotherCallAndTheFirstGoesOn()
        {
            RecordingHook hook = new();
            using FragmentingChatClient reply = new("hello");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml, reply, options => options.UseHooks(hook, new CallDecider(gate => gate.Accept("thread-1"))));
            await using FakeRelayClient first = await host.ConnectAsync();
            await using FakeRelayClient second = await host.ConnectAsync();
            await first.SendAsync(RelayFrames.Setup(conversationSessionId: "logical-7"));
            _ = await hook.WaitForAsync<CallStarted>();

            await second.SendAsync(RelayFrames.Setup(conversationSessionId: "logical-8"));
            (WebSocketCloseStatus? Status, string? Description) refused = await second.ReadCloseAsync();
            await first.SendAsync(RelayFrames.Prompt("hi", last: true));

            Assert.Equal((WebSocketCloseStatus.PolicyViolation, "the conversation is held by another call"), refused);
            Assert.Equal("hello", string.Concat(await first.ReadTextFramesUntilLastAsync()));
        }

        // PhoneCall.AdmitAsync: every admitted call reaches StartAsync or AbandonAsync, so a store that is down at the
        // start leaves the id free for the next call.
        [Fact(Timeout = 30_000)]
        public async Task AStartThatFaultsFreesTheConversationForTheNextCall()
        {
            FailingCreateStore store = new();
            using FragmentingChatClient reply = new("hello");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(StoreYaml, reply, options => options.UseConversationStores(store));
            await using FakeRelayClient first = await host.ConnectAsync();
            await first.SendAsync(RelayFrames.Setup());
            (WebSocketCloseStatus? status, _) = await first.ReadCloseAsync();
            store.Failing = false;

            await using FakeRelayClient second = await host.ConnectAsync();
            await second.SendAsync(RelayFrames.Setup());
            await second.SendAsync(RelayFrames.Prompt("hi", last: true));

            Assert.Equal(WebSocketCloseStatus.InternalServerError, status);
            Assert.Equal("hello", string.Concat(await second.ReadTextFramesUntilLastAsync()));
        }

        // The caller hung up while the start opened the store: the cancelled call is abandoned and frees the id.
        [Fact(Timeout = 30_000)]
        public async Task AHangUpWhileTheStartOpensTheStoreFreesTheConversationForTheNextCall()
        {
            GatedCreateStore store = new();
            using FragmentingChatClient reply = new("hello");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(StoreYaml, reply, options => options.UseConversationStores(store));
            await using FakeRelayClient first = await host.ConnectAsync();
            await first.SendAsync(RelayFrames.Setup());
            await store.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

            first.Abort();
            await host.WaitForConversationEndAsync("conversation-one");
            await using FakeRelayClient second = await host.ConnectAsync();
            await second.SendAsync(RelayFrames.Setup());
            await second.SendAsync(RelayFrames.Prompt("hi", last: true));

            Assert.Equal("hello", string.Concat(await second.ReadTextFramesUntilLastAsync()));
        }

        // Parity with the OpenAI Live adapter: a relay call the host's shutdown ends carries the cause "shutdown".
        [Fact(Timeout = 30_000)]
        public async Task AHostThatStopsUnderALiveCallEndsItWithTheShutdownCause()
        {
            RecordingHook hook = new();
            using FragmentingChatClient reply = new("hello");
            await using RelayConnectionHarness harness = await RelayConnectionHarness.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml,
                reply,
                configure: options => _ = options.UseHooks(hook));
            harness.Socket.Queue(RelayFrames.Setup());
            _ = await hook.WaitForAsync<CallStarted>();

            harness.StopApplication();
            ConversationEnded ended = await hook.WaitForAsync<ConversationEnded>();

            Assert.Equal((ConversationEndReason.Faulted, (string?)"shutdown"), (ended.Reason, ended.Call!.Cause));
        }
    }
}

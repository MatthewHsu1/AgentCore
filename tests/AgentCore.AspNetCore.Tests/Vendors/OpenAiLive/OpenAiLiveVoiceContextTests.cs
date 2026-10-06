using System.Text.Json.Nodes;
using AgentCore.Application.Conversation.Commands;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Session;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>
    /// A host can tell the voice a fact it looked up after the call was answered. OpenAI's live guide sends one as
    /// <c>session.thinking.append</c> with <c>delegation_id: null</c>: "factual context without asking the model to say
    /// it immediately".
    /// </summary>
    public sealed class OpenAiLiveVoiceContextTests
    {
        private const string Fact = "A staff note from this caller's earlier chat, for context only.\n\"Ask first.\"";

        [Fact(Timeout = 30_000)]
        public async Task AFactForTheVoiceGoesOutAtOnceAsSilentSessionContext()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), []);
            _ = await running.Sideband.WaitForSentAsync(IsGreeting);

            ChannelCommandResult result = running.Call.Session.Send(new AddVoiceContextCommand(Fact));
            JsonObject fact = await running.Sideband.WaitForSentAsync(IsThinking);

            Assert.Equal(ChannelCommandResult.Scheduled, result);
            Assert.Null(fact["delegation_id"]);
            Assert.Equal(Fact, (string?)fact["content"]);
        }

        // The guide sends nothing before session.started, and the voice should know the fact before it greets.
        [Fact(Timeout = 30_000)]
        public async Task AFactSentBeforeTheSessionStartedWaitsForItAndGoesBeforeTheGreeting()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), [], started: false);
            ChannelCommandResult result = running.Call.Session.Send(new AddVoiceContextCommand(Fact));
            await running.Sideband.WaitForDrainAsync();
            bool sentBeforeTheStart = running.Sideband.Sent.Count > 0;

            running.Sideband.Push([RunningLiveCall.Started]);
            _ = await running.Sideband.WaitForSentAsync(IsGreeting);

            Assert.Equal(ChannelCommandResult.Scheduled, result);
            Assert.False(sentBeforeTheStart);
            List<JsonObject> sent = [.. running.Sideband.Sent];
            Assert.InRange(sent.FindIndex(IsThinking), 0, sent.FindIndex(IsGreeting) - 1);
        }

        // CallStarted comes before the sideband is attached, so the call's channel must already be there to take the fact.
        [Fact(Timeout = 30_000)]
        public async Task AFactAHookSendsFromCallStartedReachesTheVoice()
        {
            TaskCompletionSource<ChannelCommandResult> told = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TellsOnStart hook = new(told);
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(new StallOnCueChatClient(), [hook], attachHeldUntil: told.Task);
            hook.Services = host.Services;

            _ = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_123"));

            Assert.Equal(ChannelCommandResult.Scheduled, await told.Task);
            FakeSideband sideband = await host.FirstAttach.Task;
            Assert.Equal(Fact, (string?)(await sideband.WaitForSentAsync(IsThinking))["content"]);
        }

        private static bool IsGreeting(JsonObject sent) => (string?)sent["content"] == RunningLiveCall.Greeting;

        private static bool IsThinking(JsonObject sent) => (string?)sent["type"] == "session.thinking.append";

        private sealed class TellsOnStart(TaskCompletionSource<ChannelCommandResult> told) : AgentHook
        {
            public IServiceProvider? Services { get; set; }

            public override ValueTask BeforeCallAsync(CallGate gate, CancellationToken cancellationToken)
            {
                gate.Accept("call_" + gate.CallId);
                return default;
            }

            public override async ValueTask OnCallStartedAsync(CallStarted notice, CancellationToken cancellationToken)
            {
                IConversationSessions sessions = Services!.GetRequiredService<IConversationSessionRegistry>().Sessions;
                ConversationSession? session = await sessions.TryGetAsync(notice.Scope.Entry!, notice.Scope.ConversationId!, cancellationToken);
                _ = told.TrySetResult(session?.Send(new AddVoiceContextCommand(Fact)) ?? ChannelCommandResult.NotSupported);
            }
        }
    }
}

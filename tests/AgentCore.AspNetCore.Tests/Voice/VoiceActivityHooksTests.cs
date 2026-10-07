using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Runtime.Session;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Tests.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice.Session;
using AgentCore.AspNetCore.Voice.Speech;
using AgentCore.AspNetCore.Voice.Turns;
using AgentCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AgentCore.AspNetCore.Tests.DependencyInjection.StartedHostFixture;

namespace AgentCore.AspNetCore.Tests.Voice
{
    public sealed class VoiceActivityHooksTests
    {
        // With no sink injected, a reply's clocks and the session's states reach the
        // conversation's hooks as notices, the readings stamped with the engine turn they belong to.
        [Fact(Timeout = 60_000)]
        public async Task AReplyAndABargeInReachTheConversationsHooksAsNotices()
        {
            RecordingHook hook = new();
            using StartedHost provider = await BuildAsync(
                OneAgentYaml,
                options =>
                {
                    _ = options.UseChatClients(_ => new RoutingChatClientFactory(new StallOnCueChatClient()));
                    _ = options.UseHooks(hook);
                });
            ConversationSession conversation = provider.GetRequiredService<EntryRegistry>().ForFactory("main").Create("c1");
            await using FakeConversationOutput output = new();
            VoiceSession voice = new(output, TimeProvider.System, NullLogger.Instance);
            VoiceActivity activity = new(voice, conversation, CancellationToken.None);

            SpeechHandle answered = activity.GenerateReply("hello", userTurnEndedAt: voice.Time.GetTimestamp());
            _ = await answered;
            SpeechHandle stalled = activity.GenerateReply(StallOnCueChatClient.Cue, userTurnEndedAt: voice.Time.GetTimestamp());
            await Poll.UntilAsync(() => output.Spoken.Any(text => text.Contains(StallOnCueChatClient.Piece, StringComparison.Ordinal)));
            await activity.InterruptByAudioActivity(StallOnCueChatClient.Piece, TimeSpan.FromMilliseconds(100));
            _ = await stalled;
            await conversation.FlushNoticesAsync();

            IReadOnlyList<TurnLatency> latencies = hook.Of<TurnLatency>();
            Assert.Contains(latencies, notice => notice is { Metric: LatencyMetric.TimeToFirstToken, Scope.TurnIndex: 0 });
            Assert.Contains(latencies, notice => notice is { Metric: LatencyMetric.TimeToFirstSpeech, Scope.TurnIndex: 0 });
            Assert.Contains(latencies, notice => notice is { Metric: LatencyMetric.TimeToReplyEnd, Scope.TurnIndex: 0 });
            Assert.Contains(latencies, notice => notice is { Metric: LatencyMetric.TimeToFirstToken, Scope.TurnIndex: 1 });
            Assert.Contains(latencies, notice => notice is { Metric: LatencyMetric.BargeIn, Scope.TurnIndex: null });
            Assert.Contains(hook.Of<VoiceStateChanged>(), change => change is { Party: VoiceParty.Agent, New: VoiceState.Speaking });
        }
    }
}

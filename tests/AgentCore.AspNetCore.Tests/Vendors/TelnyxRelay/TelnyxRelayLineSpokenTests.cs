using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice.Options;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay
{
    /// <summary>On the relay: what the caller said, and what the caller heard.</summary>
    public sealed class TelnyxRelayLineSpokenTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

        // The first turn moves the machine into a terminal stage, so the engine ends the conversation in the turn
        // whose reply the caller is still hearing.
        private const string EndsAfterTheFirstTurnYaml =
            """
            apiVersion: agentcore/v1
            agents:
              defaults:
                model: { ref: reply }
              items:
                - { id: greeter, instructions: "greet the caller" }
                - { id: closer,  instructions: "close the conversation" }
            entries:
              main:
                policy:
                  initial: greeting
                  stages:
                    - { id: greeting, agent: greeter, to: [ { stage: close } ] }
                    - { id: close,    agent: closer,  terminal: true }
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: reply }
            """;

        // The relay reports no end of playback, so a reply nobody talked over is heard in full once the call ends.
        [Fact(Timeout = 30_000)]
        public async Task AFinalPromptAndAFullyPlayedReplyAreTwoLines()
        {
            RecordingHook hook = new();
            using FragmentingChatClient reply = new("hello there caller");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(TelnyxRelayTurnTests.PolicyYaml, reply, options => _ = options.UseHooks(hook));
            FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup());
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            await ReplySentAsync(hook);
            await relay.DisposeAsync();
            _ = await hook.WaitForAsync<LineSpoken>(line => line.Speaker == Speaker.Agent);

            Assert.Equal([(Speaker.Caller, "hi"), (Speaker.Agent, "hello there caller")], Lines(hook));
        }

        // The caller talked over the reply after all its text went to the relay, which still plays it.
        [Fact(Timeout = 30_000)]
        public async Task ABargeInAfterTheWholeReplyWasSentCutsItsLine()
        {
            RecordingHook hook = new();
            using FragmentingChatClient reply = new("hello there caller");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(TelnyxRelayTurnTests.PolicyYaml, reply, options => _ = options.UseHooks(hook));
            await using FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup());
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            await ReplySentAsync(hook);
            await relay.SendAsync(RelayFrames.Interrupt("hello there", durationMs: 400));
            _ = await hook.WaitForAsync<LineSpoken>(line => line.Speaker == Speaker.Agent);

            Assert.Equal([(Speaker.Caller, "hi"), (Speaker.Agent, "hello there")], Lines(hook));
        }

        // The caller's next words leave the window of the reply before them open for the report's wait,
        // and with no report its line is the whole reply; it needs no call end.
        [Fact(Timeout = 30_000)]
        public async Task TheCallersNextWordsWithNoReportReleaseTheWholeLineAfterTheWait()
        {
            RecordingHook hook = new();
            FakeTimeProvider clock = new(DateTimeOffset.UnixEpoch);
            using FragmentingChatClient reply = new("hello there caller", "you are welcome");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml, reply, options => { _ = options.UseHooks(hook); options.TimeProvider = clock; });
            await using FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup());
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            await ReplySentAsync(hook);
            await relay.SendAsync(RelayFrames.Prompt("thanks", last: true));
            await clock.WaitForTimersAsync(clock.GetUtcNow() + VoiceOptions.DefaultHeardTextWait, 1).WaitAsync(Bound, TestContext.Current.CancellationToken);
            clock.Advance(VoiceOptions.DefaultHeardTextWait);
            _ = await hook.WaitForAsync<LineSpoken>(line => line.Text == "thanks");

            Assert.Equal([(Speaker.Caller, "hi"), (Speaker.Agent, "hello there caller"), (Speaker.Caller, "thanks")], Lines(hook));
        }

        // The relay sends the final prompt first and its report of what was heard after it
        // (TelnyxRelayFinalPromptCutTests); a report inside the wait still cuts the line, which keeps its place.
        [Fact(Timeout = 30_000)]
        public async Task AReportThatFollowsTheCallersNextWordsStillCutsTheLine()
        {
            RecordingHook hook = new();
            FakeTimeProvider clock = new(DateTimeOffset.UnixEpoch);
            using HeldPromptChatClient reply = new("hello there caller", "you are welcome");
            reply.ReleaseFirstTurn();
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(
                TelnyxRelayTurnTests.PolicyYaml, reply, options => { _ = options.UseHooks(hook); options.TimeProvider = clock; });
            await using FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup());
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            await ReplySentAsync(hook);
            await relay.SendAsync(RelayFrames.Prompt("thanks", last: true));

            // The next reply holds before its first word, so the report can only be about the reply before it.
            await reply.SecondTurnStarted.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            await relay.SendAsync(RelayFrames.Interrupt("hello there", durationMs: 400));
            _ = await hook.WaitForAsync<LineSpoken>(line => line.Text == "thanks");

            Assert.Equal([(Speaker.Caller, "hi"), (Speaker.Agent, "hello there"), (Speaker.Caller, "thanks")], Lines(hook));
            reply.ReleaseSecondTurn();
        }

        // The cut scenario of TelnyxRelayFinalPromptCutTests.AFinalPromptMidStep_ThenTheRelaysReport_KeepsTheHeardText:
        // its stored rows ("Hello", then "Sure.") are what the caller heard, in the order it was said.
        [Fact(Timeout = 30_000)]
        public async Task ACutReplyIsTheLineTheCallerHeard()
        {
            RecordingHook hook = new();
            GatedStepChatClient model = new();
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(
                TwoStepChatClient.Yaml, model, options => { TwoStepChatClient.BindTool(options); _ = options.UseHooks(hook); });
            FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup(conversationSessionId: "line-cut"));
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            await TelnyxRelayFinalPromptCutTests.ReadTokensUntilAsync(relay, "Hello there, ");
            await model.Held.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            await relay.SendAsync(RelayFrames.Prompt("wait", last: true));
            await model.Stopped.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            await relay.SendAsync(RelayFrames.Interrupt("Hello", durationMs: 400));
            await ReplySentAsync(hook);
            await relay.DisposeAsync();
            _ = await hook.WaitForAsync<LineSpoken>(line => line.Text == "Sure.");

            Assert.Equal(
                [(Speaker.Caller, "hi"), (Speaker.Agent, "Hello"), (Speaker.Caller, "wait"), (Speaker.Agent, "Sure.")],
                Lines(hook));
        }

        // A spoken line is let through after ConversationEnded: it is a record of the call, not a turn
        // notice. So the reply of the turn that ended the conversation still carries its turn, and the caller's words
        // after the end, before the call closes, are still a line.
        [Fact(Timeout = 30_000)]
        public async Task TheReplyOfTheTurnThatEndedTheConversationAndTheCallersWordsAfterItAreStillLines()
        {
            RecordingHook hook = new();
            using FragmentingChatClient reply = new("goodbye then");
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(EndsAfterTheFirstTurnYaml, reply, options => _ = options.UseHooks(hook));
            FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup());
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            await ReplySentAsync(hook);
            ConversationEnded ended = await hook.WaitForAsync<ConversationEnded>();
            await relay.SendAsync(RelayFrames.Prompt("okay bye", last: true));
            await relay.DisposeAsync();
            _ = await hook.WaitForAsync<ConversationUnloaded>().WaitAsync(Bound, TestContext.Current.CancellationToken);

            TurnStarted turn = Assert.Single(hook.Of<TurnStarted>());
            LineSpoken line = hook.Of<LineSpoken>().First(line => line.Speaker == Speaker.Agent);
            Assert.Equal([(Speaker.Caller, "hi"), (Speaker.Agent, "goodbye then"), (Speaker.Caller, "okay bye")], Lines(hook));
            Assert.Equal(turn.Scope.TurnIndex, line.Scope.TurnIndex);
            Assert.Equal(ConversationEndReason.AgentCompleted, ended.Reason);
        }

        // The reply's end latency is read as its last step ends, after every word went to the relay.
        private static async Task ReplySentAsync(RecordingHook hook)
        {
            _ = await hook.WaitForAsync<TurnLatency>(latency => latency.Metric == LatencyMetric.TimeToReplyEnd)
                .WaitAsync(Bound, TestContext.Current.CancellationToken);
        }

        private static IEnumerable<(Speaker Speaker, string Text)> Lines(RecordingHook hook)
        {
            return hook.Of<LineSpoken>().Select(line => (line.Speaker, line.Text));
        }
    }
}

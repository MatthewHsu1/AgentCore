using System.Threading.Channels;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Sessions.Memory;
using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay;
using AgentCore.AspNetCore.Voice.Filler;
using AgentCore.AspNetCore.Voice.Options;
using AgentCore.AspNetCore.Voice.Ports;
using AgentCore.AspNetCore.Voice.Transport;
using AgentCore.AspNetCore.Voice.Turns;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary><see cref="AgentCore.AspNetCore.Voice.Turns.VoiceConversationLoop"/> over real conversation sessions.</summary>
    public sealed class VoiceConversationLoopTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // A second Started mid-speech lets the running speech finish on its own conversation, and the next turn
        // runs on the new one. Nothing is held.
        [Fact(Timeout = 30_000)]
        public async Task ASecondConversationNamedMidSpeech_LetsTheSpeechFinishAndTheNextTurnRunsOnTheNewOne()
        {
            using HeldPromptChatClient reply = new("first reply", "second reply");
            FakeConversationOutput output = new();
            (VoiceLoopHarness harness, Task running) = VoiceLoopHarness.Start(reply, output);

            harness.Send(new ConversationInput.Started("conversation-replaced"));
            harness.Send(new ConversationInput.Utterance("one", "en", IsFinal: true));
            await reply.WaitUntilFirstTurnStreamingAsync().WaitAsync(Ct);
            ConversationSession replaced = (await harness.Sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-replaced", Ct))!;

            harness.Send(new ConversationInput.Started("conversation-named"));
            await Poll.UntilAsync(() => harness.Loop.ConversationId == "conversation-named");
            reply.ReleaseFirstTurn();
            await Poll.UntilAsync(() => output.Completions == 1);

            harness.Send(new ConversationInput.Utterance("two", "en", IsFinal: true));
            await reply.SecondTurnStarted.Task.WaitAsync(Ct);
            reply.ReleaseSecondTurn();
            await Poll.UntilAsync(() => output.Completions == 2);
            harness.Complete();
            await running;
            await harness.Loop.DrainAsync();

            ConversationSession named = (await harness.Sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-named", Ct))!;
            // "two" may still find the first speech current once its words are out, and interrupt it there; the
            // turn then keeps every word it forwarded, so either way its reply is whole.
            Assert.Equal("first reply", replaced.LastTurn!.ReplyText);
            Assert.Equal(["two", "second reply"], named.Transcript.Select(message => message.Text));
            Assert.Equal("first reply" + "second reply", string.Concat(output.Spoken));
        }

        // A barge-in before any conversation started has no speech to cut, so the loop reads on.
        [Fact(Timeout = 30_000)]
        public async Task ABargeInBeforeTheConversationStarted_IsIgnored()
        {
            using HeldPromptChatClient reply = new("first reply", "second reply");
            FakeConversationOutput output = new();
            (VoiceLoopHarness harness, Task running) = VoiceLoopHarness.Start(reply, output);

            harness.Send(new ConversationInput.Barge("hello", TimeSpan.FromMilliseconds(300)));
            harness.Complete();
            await running;

            Assert.Equal(0, output.Stops);
            Assert.False(harness.Loop.HasStarted);
        }

        // The caller hung up on a call this loop's session had already been idle-unloaded out from under, and
        // a second call reopened the same id under the same entry in between. The stale hang-up must leave the
        // newer session, its folder, and the audit chain it is writing to alone.
        [Fact(Timeout = 30_000)]
        public async Task AHangUpFromAStaleLoopLeavesANewerSessionOnTheSameIdUntouched()
        {
            TimeSpan idleTimeout = TimeSpan.FromMinutes(1);
            FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
            string root = Path.Combine(Path.GetTempPath(), "agentcore-voice-p1-" + Guid.NewGuid().ToString("N"));

            try
            {
                RecordingHook hook = new();
                using HeldPromptChatClient reply = new("unused", "unused");
                AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(TelnyxRelayTurnTests.PolicyYaml);
                RoutingChatClientFactory chatClients = new(reply);
                CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                    document, new AgentCompilationContext(chatClients) { WorkspaceRoot = root })[SingleEntrySessionFactories.MainEntry];
                ConversationSessionFactory factory = new(
                    compiled,
                    new GuardEvaluator(compiled.Configuration.Guards),
                    extractor: null,
                    timeProvider: clock,
                    workspaceRoot: root,
                    hooks: [hook]);

                using InMemoryConversationSessions sessions = new(SingleEntrySessionFactories.Of(factory), idleTimeout, clock);

                VoiceConversationLoop loopA = NewLoop(sessions, clock, new FakeConversationOutput());
                await loopA.RunAsync(OneAsync(new ConversationInput.Started("conversation-1")));

                ConversationSession stale = (await sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Ct))!;

                clock.Advance(idleTimeout);
                await Poll.UntilAsync(() => !Directory.Exists(stale.Workspace!));

                VoiceConversationLoop loopB = NewLoop(sessions, clock, new FakeConversationOutput());
                await loopB.RunAsync(OneAsync(new ConversationInput.Started("conversation-1")));

                ConversationSession fresh = (await sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Ct))!;
                Assert.NotSame(stale, fresh);

                string sentinel = Path.Combine(fresh.Workspace!, "sentinel.txt");
                await File.WriteAllTextAsync(sentinel, "loop B's file", Ct);

                await loopA.EndAsync(ConversationEndReason.CallerHungUp);

                Assert.True(File.Exists(sentinel), "the stale hang-up must not delete the newer session's folder.");
                Assert.Same(fresh, await sessions.TryGetAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", Ct));
                await fresh.FlushNoticesAsync();
                Assert.Empty(hook.Of<ConversationEnded>());
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        // Each line waits on a store lookup before it is raised, and the order of LineSpoken is the transcript a CRM
        // keeps: a line whose lookup is slow must not be overtaken by the line said after it.
        [Fact(Timeout = 30_000)]
        public async Task TwoFinalPromptsBackToBackAreLinesInTheOrderTheyWereSaid()
        {
            RecordingHook hook = new();
            using FragmentingChatClient reply = new("first reply", "second reply");
            using InMemoryConversationSessions inner = HookedSessions(reply, hook);
            GatedConversationSessions sessions = new(inner);
            FakeConversationOutput output = new();
            Channel<ConversationInput> inputs = Channel.CreateUnbounded<ConversationInput>();
            VoiceConversationLoop loop = NewLoop(sessions, TimeProvider.System, output);
            Task running = loop.RunAsync(inputs.Reader.ReadAllAsync(Ct));

            // The first prompt's keep-alive lookup passes; the lookup its line makes is held.
            sessions.HoldTryGet(2);
            _ = inputs.Writer.TryWrite(new ConversationInput.Started("conversation-lines"));
            _ = inputs.Writer.TryWrite(new ConversationInput.Utterance("one", "en", IsFinal: true));
            await sessions.TryGetHeld.Task.WaitAsync(Ct);
            _ = inputs.Writer.TryWrite(new ConversationInput.Utterance("two", "en", IsFinal: true));

            // The loop reads in order, so once the barge after "two" reached the output, the line of "two" was handed on.
            _ = inputs.Writer.TryWrite(new ConversationInput.Barge("first", TimeSpan.FromMilliseconds(100)));
            await Poll.UntilAsync(() => output.Stops == 1);
            sessions.ReleaseTryGet();
            _ = await hook.WaitForAsync<LineSpoken>(line => line.Text == "two").WaitAsync(Ct);

            Assert.Equal(["one", "two"], hook.Of<LineSpoken>().Where(line => line.Speaker == Speaker.Caller).Select(line => line.Text));

            _ = inputs.Writer.TryComplete();
            await running;
            await loop.DrainAsync();
            await loop.EndAsync(ConversationEndReason.CallerHungUp);
        }

        // The connection ends the call right after the drain: a line still waiting on its store lookup must be raised
        // before that, or the ended call drops it.
        [Fact(Timeout = 30_000)]
        public async Task TheDrainWaitsForTheLinesStillQueued()
        {
            RecordingHook hook = new();
            using FragmentingChatClient reply = new("first reply", "second reply");
            using InMemoryConversationSessions inner = HookedSessions(reply, hook);
            GatedConversationSessions sessions = new(inner);
            Channel<ConversationInput> inputs = Channel.CreateUnbounded<ConversationInput>();
            VoiceConversationLoop loop = NewLoop(sessions, TimeProvider.System, new FakeConversationOutput());
            Task running = loop.RunAsync(inputs.Reader.ReadAllAsync(Ct));

            _ = inputs.Writer.TryWrite(new ConversationInput.Started("conversation-drain"));
            _ = inputs.Writer.TryWrite(new ConversationInput.Utterance("one", "en", IsFinal: true));
            _ = await hook.WaitForAsync<TurnLatency>(latency => latency.Metric == LatencyMetric.TimeToReplyEnd).WaitAsync(Ct);
            _ = inputs.Writer.TryComplete();
            await running;

            // Nothing else reads the store now: the next lookup is the one the agent's line makes.
            sessions.HoldTryGet(1);
            Task drained = loop.DrainAsync();
            await sessions.TryGetHeld.Task.WaitAsync(Ct);
            Assert.False(drained.IsCompleted, "the drain must wait for the line still on its way.");
            sessions.ReleaseTryGet();
            await drained;
            _ = await hook.WaitForAsync<LineSpoken>(line => line.Speaker == Speaker.Agent).WaitAsync(Ct);
            await loop.EndAsync(ConversationEndReason.CallerHungUp);

            Assert.Equal(
                [(Speaker.Caller, "one"), (Speaker.Agent, "first reply")],
                hook.Of<LineSpoken>().Select(line => (line.Speaker, line.Text)));
        }

        // The caller hangs up right after speaking: the connection is cancelled while the loop still touches the
        // store for the words it already read. They are still a line, or the transcript loses the caller's last words.
        [Fact(Timeout = 30_000)]
        public async Task WordsReadBeforeTheConnectionWentAreStillALine()
        {
            RecordingHook hook = new();
            using FragmentingChatClient reply = new("first reply", "second reply");
            using InMemoryConversationSessions inner = HookedSessions(reply, hook);
            GatedConversationSessions sessions = new(inner);
            using CancellationTokenSource connection = new();
            Channel<ConversationInput> inputs = Channel.CreateUnbounded<ConversationInput>();
            VoiceConversationLoop loop = NewLoop(sessions, TimeProvider.System, new FakeConversationOutput(), connection);
            Task running = loop.RunAsync(inputs.Reader.ReadAllAsync(Ct));

            sessions.HoldTryGet(1);
            _ = inputs.Writer.TryWrite(new ConversationInput.Started("conversation-hung-up"));
            _ = inputs.Writer.TryWrite(new ConversationInput.Utterance("okay bye", "en", IsFinal: true));
            await sessions.TryGetHeld.Task.WaitAsync(Ct);
            await connection.CancelAsync();
            sessions.ReleaseTryGet();
            _ = inputs.Writer.TryComplete();

            Assert.Null(await Record.ExceptionAsync(() => running));
            await loop.DrainAsync();
            _ = await hook.WaitForAsync<LineSpoken>(line => line.Speaker == Speaker.Caller).WaitAsync(Ct);
            await loop.EndAsync(ConversationEndReason.CallerHungUp);

            Assert.Equal(["okay bye"], hook.Of<LineSpoken>().Where(line => line.Speaker == Speaker.Caller).Select(line => line.Text));
        }

        private static InMemoryConversationSessions HookedSessions(IChatClient reply, RecordingHook hook)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(TelnyxRelayTurnTests.PolicyYaml);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document, new AgentCompilationContext(new RoutingChatClientFactory(reply)))[SingleEntrySessionFactories.MainEntry];
            ConversationSessionFactory factory = new(compiled, new GuardEvaluator(compiled.Configuration.Guards), extractor: null, hooks: [hook]);
            return new InMemoryConversationSessions(SingleEntrySessionFactories.Of(factory), TimeSpan.FromMinutes(5), TimeProvider.System);
        }

        private static VoiceConversationLoop NewLoop(
            IConversationSessions sessions, TimeProvider clock, IConversationOutputPort output, CancellationTokenSource? connection = null)
        {
            VoiceConversationLoop? loop = null;
            loop = new VoiceConversationLoop(
                new PhoneCallHost(sessions, HookRuntime.Empty, clock, NullLogger.Instance, GatePoint.BeforeCall.Deadline),
                SingleEntrySessionFactories.MainEntry,
                "test-transport",
                output,
                new ConnectionTaskObserver(() => loop?.ConversationId ?? "(none)", (_, _) => { }, (_, _, _) => { }, (_, _, _) => false),
                clock,
                NullLogger.Instance,
                TimeSpan.FromSeconds(5),
                connection?.Token ?? CancellationToken.None,
                new VoiceOptions(UserAway: null, new Dictionary<string, FillerOptions>()));
            return loop;
        }

        private static async IAsyncEnumerable<ConversationInput> OneAsync(ConversationInput input)
        {
            await Task.Yield();
            yield return input;
        }
    }
}

using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Sessions.Memory;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay;
using AgentCore.AspNetCore.Voice;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary><see cref="AgentCore.AspNetCore.Voice.VoiceConversationLoop"/> over real conversation sessions.</summary>
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
                RecordingObserver observer = new();
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
                    observers: [observer],
                    workspaceRoot: root);

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
                Assert.DoesNotContain(ConversationEventKind.ConversationEnded, observer.Kinds);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        private static VoiceConversationLoop NewLoop(InMemoryConversationSessions sessions, TimeProvider clock, IConversationOutputPort output)
        {
            VoiceConversationLoop? loop = null;
            loop = new VoiceConversationLoop(
                sessions,
                SingleEntrySessionFactories.MainEntry,
                output,
                new ConnectionTaskObserver(() => loop?.ConversationId ?? "(none)", (_, _) => { }, (_, _, _) => { }, (_, _, _) => false),
                clock,
                NullLogger.Instance,
                TimeSpan.FromSeconds(5),
                CancellationToken.None,
                new VoiceOptions(UserAway: null, new Dictionary<string, FillerOptions>()));
            return loop;
        }

        private static async IAsyncEnumerable<ConversationInput> OneAsync(ConversationInput input)
        {
            await Task.Yield();
            yield return input;
        }

        private sealed class RecordingObserver : IConversationObserver
        {
            private readonly List<ConversationEventKind> _kinds = [];

            public IReadOnlyList<ConversationEventKind> Kinds
            {
                get
                {
                    lock (_kinds)
                    {
                        return [.. _kinds];
                    }
                }
            }

            public ValueTask OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken = default)
            {
                lock (_kinds)
                {
                    _kinds.Add(conversationEvent.Kind);
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}

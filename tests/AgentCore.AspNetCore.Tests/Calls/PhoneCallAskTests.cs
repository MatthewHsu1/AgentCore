using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Calls
{
    /// <summary>A call's asks, and a correction while one is open.</summary>
    public sealed class PhoneCallAskTests
    {
        private const string Question = "Max speed of the F63?";

        private const string Correction = "Oh wait, I meant the F80";

        private const string EntryFallback = "Sorry, I lost you there. Please call back.";

        private const string EntryFallbackYaml = """
        apiVersion: agentcore/v1
        fallbackReply: "The document's own fallback."
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "answer the caller" }
        entries:
          main:
            agent: only
            fallbackReply: "Sorry, I lost you there. Please call back."
        """;

        // The fast tool comes first, so it has returned by the time the slow one holds the round.
        private const string TwoToolsYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: stock_lookup, kind: builtin, uses: orders.read, description: "Look up the stock of an item." }
          - { id: price_lookup, kind: builtin, uses: orders.read, description: "Look up the price of an item." }
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "answer the caller", tools: [ stock_lookup, price_lookup ] }
        entries:
          main:
            agent: only
        """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task AnAskIsAnsweredWithTheModelsReply()
        {
            StallOnCueChatClient model = new();
            (PhoneCallHarness harness, PhoneCall call) = await StartedAsync(model, []);
            using (harness)
            {
                CallAnswer answer = await call.Asks.AskAsync("d1", "What are your hours?", [], NoTools, Ct);

                Assert.Equal(CallAnswer.Answered("reply to What are your hours?"), answer);
            }
        }

        // Once the older answer went out, a correction is a turn of its own.
        [Fact]
        public async Task ACorrectionAfterTheOlderAnswerKeepsTheOlderTurn()
        {
            RecordingHook hook = new();
            StallOnCueChatClient model = new();
            (PhoneCallHarness harness, PhoneCall call) = await StartedAsync(model, [hook]);
            using (harness)
            {
                _ = await call.Asks.AskAsync("d1", Question, [], NoTools, Ct);
                CallAnswer second = await call.Asks.AskAsync("d2", Correction, [], NoTools, Ct);
                await call.Session.FlushNoticesAsync();

                Assert.Equal(CallAnswer.Answered("reply to " + Correction), second);
                Assert.Equal([Question, Correction], UserWords(model.Requests[^1]));
                Assert.Empty(hook.Of<TurnSuperseded>());
            }
        }

        // Tool progress goes to the vendor as it happens.
        [Fact]
        public async Task EachToolCallIsReportedAsProgress()
        {
            (PhoneCallHarness harness, PhoneCall call) = await StartedAsync(new GatedToolCallingChatClient(), []);
            using (harness)
            {
                List<string> tools = [];

                _ = await call.Asks.AskAsync("d1", "How much is the F80?", [], name => { tools.Add(name); return default; }, Ct);

                Assert.Equal(["price_lookup"], tools);
            }
        }

        // The caller hung up, so the running ask is withdrawn and the call ends once.
        [Fact(Timeout = 10_000)]
        public async Task AHangUpWithdrawsTheRunningAsk()
        {
            RecordingHook hook = new();
            StallOnCueChatClient model = new() { HoldFirst = true };
            (PhoneCallHarness harness, PhoneCall call) = await StartedAsync(model, [hook]);
            using (harness)
            {
                Task<CallAnswer> asking = call.Asks.AskAsync("d1", Question, [], NoTools, Ct);
                await model.FirstEntered.Task;

                await call.EndAsync(ConversationEndReason.CallerHungUp, "close_requested");
                await call.Session.FlushNoticesAsync();

                Assert.Equal(CallAnswer.Withdrawn, await asking);
                Assert.Equal("close_requested", Assert.Single(hook.Of<ConversationEnded>()).Call!.Cause);
            }
        }

        // The caller hears the engine's own fallback for the entry: its fallbackReply over the document's.
        [Fact]
        public async Task AnAskAfterTheCallEndedGetsTheEntrysFallbackReply()
        {
            (PhoneCallHarness harness, PhoneCall call) = await StartedAsync(new StallOnCueChatClient(), [], yaml: EntryFallbackYaml);
            using (harness)
            {
                await call.EndAsync(ConversationEndReason.CallerHungUp, cause: null);

                CallAnswer answer = await call.Asks.AskAsync("d9", "hello?", [], NoTools, Ct);

                Assert.Equal(new CallAnswer(CallAnswerKind.Fallback, EntryFallback), answer);
            }
        }

        [Fact]
        public async Task AFaultWhileTheTurnRunsGetsTheFallbackReply()
        {
            (PhoneCallHarness harness, PhoneCall call) = await StartedAsync(new GatedToolCallingChatClient(), []);
            using (harness)
            {
                CallAnswer answer = await call.Asks.AskAsync("d1", "How much is the F80?", [], _ => throw new IOException("the vendor socket closed"), Ct);

                Assert.Equal(new CallAnswer(CallAnswerKind.Fallback, AgentCoreConfiguration.DefaultFallbackReply), answer);
            }
        }

        // A store or HTTP timeout cancels with a token that is not the ask's: a fault, not a withdraw.
        [Fact]
        public async Task ATimeoutWhileTheSessionReopensGetsTheFallbackReply()
        {
            GatedConversationSessions? sessions = null;
            (PhoneCallHarness harness, PhoneCall call) = await StartedAsync(
                new StallOnCueChatClient(), [], host => host with { Sessions = sessions = new GatedConversationSessions(host.Sessions) });
            using (harness)
            {
                sessions!.Fault(new TaskCanceledException("the store timed out"));

                CallAnswer answer = await call.Asks.AskAsync("d1", Question, [], NoTools, Ct);

                Assert.Equal(new CallAnswer(CallAnswerKind.Fallback, AgentCoreConfiguration.DefaultFallbackReply), answer);
            }
        }

        // Asks take their place in the order they arrive, even while the older one still waits for its session.
        [Fact(Timeout = 10_000)]
        public async Task ACorrectionWhileTheOlderAskWaitsForItsSessionReplacesIt()
        {
            GatedConversationSessions? sessions = null;
            (PhoneCallHarness harness, PhoneCall call) = await StartedAsync(
                new StallOnCueChatClient(), [], host => host with { Sessions = sessions = new GatedConversationSessions(host.Sessions) });
            using (harness)
            {
                sessions!.Arm();
                Task<CallAnswer> older = call.Asks.AskAsync("d1", Question, [], NoTools, Ct);
                await sessions.Entered.Task;

                CallAnswer newer = await call.Asks.AskAsync("d2", Correction, [], NoTools, Ct);
                sessions.Release();

                Assert.Equal(CallAnswer.Withdrawn, await older);
                Assert.Equal(CallAnswer.Answered("reply to " + Question + " " + Correction), newer);
            }
        }

        // The correction replaces the unanswered question; the model reads the whole thought once.
        [Fact(Timeout = 10_000)]
        public async Task ACorrectionWhileTheOlderAskRunsReplacesIt()
        {
            RecordingHook hook = new();
            StallOnCueChatClient model = new() { HoldFirst = true };
            (PhoneCallHarness harness, PhoneCall call) = await StartedAsync(model, [hook]);
            using (harness)
            {
                Task<CallAnswer> older = call.Asks.AskAsync("d1", Question, [], NoTools, Ct);
                await model.FirstEntered.Task;

                CallAnswer newer = await call.Asks.AskAsync("d2", Correction, [], NoTools, Ct);
                await call.Session.FlushNoticesAsync();

                Assert.Equal(CallAnswer.Withdrawn, await older);
                Assert.Equal(CallAnswer.Answered("reply to " + Question + " " + Correction), newer);
                Assert.Equal([Question + " " + Correction], UserWords(model.Requests[^1]));
                Assert.DoesNotContain(call.Session.Transcript, message => message.Text == Question);
                _ = Assert.Single(hook.Of<TurnSuperseded>());
            }
        }

        // The withdrawn ask's running tool finishes, and the correction's
        // turn reads its result instead of running it again.
        [Fact(Timeout = 10_000)]
        public async Task ACorrectionWhileTheOlderAsksToolRunsLetsItFinishOnceAndTheNewTurnReadsItsResult()
        {
            GatedTool tool = new();
            (PhoneCallHarness harness, PhoneCall call) = await StartedAsync(
                new GatedToolCallingChatClient(), [], tools: declared => tool.Create(declared.Id, declared.Description ?? declared.Id));
            using (harness)
            {
                Task<CallAnswer> older = call.Asks.AskAsync("d1", Question, [], NoTools, Ct);
                await tool.Entered.Task;

                Task<CallAnswer> newer = call.Asks.AskAsync("d2", Correction, [], NoTools, Ct);
                tool.Release.SetResult();

                Assert.Equal(CallAnswer.Withdrawn, await older);
                Assert.Equal(CallAnswer.Answered("done."), await newer);
                Assert.Equal((1, 1), (tool.Runs, tool.Finished));
                Assert.Contains(
                    call.Session.Transcript.SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
                    result => result.Result?.ToString() == GatedTool.Result);
                Assert.DoesNotContain(call.Session.Transcript, message => message.Text == Question);
            }
        }

        // When the older ask's tool outlives its withdraw: the correction's turn waits for the tool and reads its
        // result. The tool is held until the correction's turn reached the model, which only a turn that did not wait
        // does; a turn that waits gets the release from the bound instead.
        [Fact(Timeout = 10_000)]
        public async Task ACorrectionWhoseOlderAsksToolOutlivesTheWithdrawStillReadsItsResult()
        {
            GatedTool tool = new();
            GatedToolCallingChatClient model = new();
            (PhoneCallHarness harness, PhoneCall call) = await StartedAsync(
                model, [], tools: declared => tool.Create(declared.Id, declared.Description ?? declared.Id));
            using (harness)
            {
                Task<CallAnswer> older = call.Asks.AskAsync("d1", Question, [], NoTools, Ct);
                await tool.Entered.Task;

                Task<CallAnswer> newer = call.Asks.AskAsync("d2", Correction, [], NoTools, Ct);
                Assert.Equal(CallAnswer.Withdrawn, await older);
                _ = await Task.WhenAny(model.SecondRequest.Task, Task.Delay(TimeSpan.FromSeconds(1), Ct));
                tool.Release.SetResult();

                Assert.Equal(CallAnswer.Answered("done."), await newer);
                Assert.Equal((1, 1), (tool.Runs, tool.Finished));
            }
        }

        // The call that returned before its sibling was carried reached no response
        // message, so it is kept too, and the correction's turn runs neither tool again.
        [Fact(Timeout = 10_000)]
        public async Task ACorrectionReadsTheResultOfACallThatFinishedBeforeItsCarriedSibling()
        {
            GatedTool price = new();
            int stockRuns = 0;
            MissingToolsChatClient model = new();
            string Stock()
            {
                _ = Interlocked.Increment(ref stockRuns);
                return "{\"stock\":3}";
            }

            (PhoneCallHarness harness, PhoneCall call) = await StartedAsync(
                model,
                [],
                yaml: TwoToolsYaml,
                tools: declared => declared.Id == "stock_lookup"
                    ? AIFunctionFactory.Create(Stock, declared.Id, declared.Description)
                    : price.Create(declared.Id, declared.Description ?? declared.Id));
            using (harness)
            {
                Task<CallAnswer> older = call.Asks.AskAsync("d1", Question, [], NoTools, Ct);
                await price.Entered.Task;

                Task<CallAnswer> newer = call.Asks.AskAsync("d2", Correction, [], NoTools, Ct);
                Assert.Equal(CallAnswer.Withdrawn, await older);
                _ = await Task.WhenAny(model.SecondRequest.Task, Task.Delay(TimeSpan.FromSeconds(1), Ct));
                price.Release.SetResult();

                Assert.Equal(CallAnswer.Answered("done."), await newer);
                Assert.Equal((1, 1), (stockRuns, price.Runs));
            }
        }

        // A call whose round wrote its response messages rides the withdrawn turn's own rows; a later round's carry must
        // not keep it a second time.
        [Fact(Timeout = 10_000)]
        public async Task ACarryInALaterRoundKeepsNoCallOfAnEarlierRoundAgain()
        {
            GatedTool price = new();
            MissingToolsChatClient model = new() { OnePerRound = true };
            (PhoneCallHarness harness, PhoneCall call) = await StartedAsync(
                model,
                [],
                yaml: TwoToolsYaml,
                tools: declared => declared.Id == "stock_lookup"
                    ? AIFunctionFactory.Create(() => "{\"stock\":3}", declared.Id, declared.Description)
                    : price.Create(declared.Id, declared.Description ?? declared.Id));
            using (harness)
            {
                Task<CallAnswer> older = call.Asks.AskAsync("d1", Question, [], NoTools, Ct);
                await price.Entered.Task;

                Task<CallAnswer> newer = call.Asks.AskAsync("d2", Correction, [], NoTools, Ct);
                Assert.Equal(CallAnswer.Withdrawn, await older);
                price.Release.SetResult();

                Assert.Equal(CallAnswer.Answered("done."), await newer);
                _ = Assert.Single(
                    call.Session.Transcript.SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
                    result => result.Result?.ToString() == "{\"stock\":3}");
            }
        }

        // What the front voice handled ahead of each ask rides the resend once, ahead of the caller's whole thought.
        [Fact(Timeout = 10_000)]
        public async Task ACorrectionWhileTheOlderAskRunsCarriesWhatWasSaidAheadOfBothAsksOnce()
        {
            StallOnCueChatClient model = new() { HoldFirst = true };
            (PhoneCallHarness harness, PhoneCall call) = await StartedAsync(model, []);
            using (harness)
            {
                Task<CallAnswer> older = call.Asks.AskAsync("d1", Question, [new(ChatRole.User, "Hi"), FrontVoice.Line("Hello! How can I help?")], NoTools, Ct);
                await model.FirstEntered.Task;

                CallAnswer newer = await call.Asks.AskAsync("d2", Correction, [new(ChatRole.User, "Are you there?"), FrontVoice.Line("Yes, one moment.")], NoTools, Ct);
                _ = await older;

                (string, string?, string)[] said =
                [
                    ("user", null, "Hi"),
                    ("assistant", FrontVoice.AuthorName, "Hello! How can I help?"),
                    ("user", null, "Are you there?"),
                    ("assistant", FrontVoice.AuthorName, "Yes, one moment."),
                    ("user", null, Question + " " + Correction),
                ];
                Assert.Equal(CallAnswer.Answered("reply to " + Question + " " + Correction), newer);
                Assert.Equal(said, Spoken(model.Requests[^1]));
                Assert.Equal([.. said, ("assistant", "only", "reply to " + Question + " " + Correction)], Spoken(call.Session.Transcript));
            }
        }

        private static ValueTask NoTools(string name)
        {
            return default;
        }

        private static List<(string Role, string? Author, string Text)> Spoken(IEnumerable<ChatMessage> messages)
        {
            return [.. messages.Where(message => message.Role != ChatRole.System).Select(message => (message.Role.Value, message.AuthorName, message.Text))];
        }

        private static List<string> UserWords(IReadOnlyList<ChatMessage> request)
        {
            return [.. request.Where(message => message.Role == ChatRole.User).Select(message => message.Text)];
        }

        private static async Task<(PhoneCallHarness Harness, PhoneCall Call)> StartedAsync(
            IChatClient model,
            IReadOnlyList<AgentHook> hooks,
            Func<PhoneCallHost, PhoneCallHost>? hostOf = null,
            string yaml = PhoneCallHarness.OneEntryYaml,
            Func<ToolConfiguration, AITool?>? tools = null)
        {
            PhoneCallHarness harness = PhoneCallHarness.Create(model, hooks, yaml, tools: tools);
            PhoneCallHost host = hostOf is null ? harness.Host : hostOf(harness.Host);
            PhoneCall call = (await PhoneCall.AdmitAsync(host, PhoneCallHarness.Offer(), Ct)).Call!;
            await call.StartAsync(Ct);
            return (harness, call);
        }
    }
}

using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Hooks;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Runtime
{
    // A cut or withdrawn turn frees the turn slot at once; its started tool runs on in the background, and its
    // finished call and result reach the history of the next turn that starts after it, as in LiveKit.
    public sealed class CutTurnBackgroundToolTests
    {
        private const string Question = "Max speed of the F63?";

        private const string Next = "hello, are you there?";

        private const string Answer = "ok";

        private const string EndsAfterTheSecondTurnYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: price_lookup, kind: builtin, uses: orders.read, description: "Look up the price of an item." }
        guards:
          afterSecond: { ">=": [ { var: turnIndex }, 2 ] }
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "quote the price", tools: [ price_lookup ] }
        entries:
          main:
            policy:
              initial: working
              stages:
                - { id: working, agent: only, to: [ { stage: done, when: afterSecond } ] }
                - { id: done, agent: only, terminal: true }
        """;

        private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact(Timeout = 20_000)]
        public async Task TheNextTurnAfterACutIsNotRefusedWhileTheCutTurnsToolStillRuns()
        {
            FakeTimeProvider time = new(Start);
            GatedTool tool = new();
            ConversationSession session = Create(new NewestWordsToolChatClient("F63", Answer), tool, time: time);

            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, Question), origin: null, Ct);
            Task reading = ReadAllAsync(run);
            await tool.Entered.Task;
            Assert.True(session.Cut(run.TurnIndex, new TurnCut(string.Empty, TimeSpan.Zero)));
            await reading.WaitAsync(TimeSpan.FromSeconds(5), Ct);

            TurnResult next = await session.RunTurnAsync(Next, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);

            Assert.Equal((Answer, 0), (next.ReplyText, tool.Finished));
            tool.Release.SetResult();
            await session.ToolRuns.WhenIdle();
        }

        // Order (a), the tool finishing before the next turn starts, is RunningToolInterruptTests'
        // ACutWhileAToolRunsLetsItFinishAndKeepsItsCallAndResult.

        // The finished call and result are stored as soon as the tool ends: a reload with no turn in between still has
        // them, a refused next turn does not lose them, and the turn after stores them no second time.
        [Theory(Timeout = 20_000)]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AToolACutTurnLeftRunningIsStoredTheMomentItFinishes(bool nextTurnRefused)
        {
            GatedTool tool = new();
            RefusingStore store = new();
            NewestWordsToolChatClient model = new("F63", Answer);
            ConversationSession session = HookSessions.Create(
                HookSessions.ToolAgentYaml,
                model,
                store: store,
                tools: declared => tool.Create(declared.Id, declared.Description ?? declared.Id),
                conversationId: "conversation-1");

            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, Question), origin: null, Ct);
            Task reading = ReadAllAsync(run);
            await tool.Entered.Task;
            Assert.True(session.Cut(run.TurnIndex, new TurnCut(string.Empty, TimeSpan.Zero)));
            await reading.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            tool.Release.SetResult();
            await session.ToolRuns.WhenIdle();
            store.Refuses = nextTurnRefused;
            _ = await Record.ExceptionAsync(() => session.RunTurnAsync(Next, Ct));
            await session.FlushTranscriptAsync();
            await session.DisposeAsync();
            store.Refuses = false;

            ConversationSession reloaded = HookSessions.Create(
                HookSessions.ToolAgentYaml,
                model,
                store: store,
                tools: declared => tool.Create(declared.Id, declared.Description ?? declared.Id),
                conversationId: "conversation-1");
            _ = await reloaded.RunTurnAsync("thanks", Ct);
            await reloaded.FlushTranscriptAsync();

            List<string> stored = Shapes((await store.ReadForSessionAsync("conversation-1", Ct)).Select(row => row.Content));
            Assert.Equal(nextTurnRefused ? [Question, "call", "result", "thanks", Answer] : [Question, "call", "result", Next, Answer, "thanks", Answer], stored);
            Assert.Equal(1, tool.Runs);
        }
        // Order (b): the tool finishes while the next turn runs. That turn's request was built without it, so the
        // call and result are stored behind that turn's rows, and the turn after reads them.
        [Fact(Timeout = 20_000)]
        public async Task AToolThatFinishesWhileTheNextTurnRunsRidesTheTurnAfter()
        {
            GatedTool tool = new();
            NewestWordsToolChatClient model = new("F63", Answer) { Held = Next };
            ConversationSession session = Create(model, tool);

            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, Question), origin: null, Ct);
            Task reading = ReadAllAsync(run);
            await tool.Entered.Task;
            Assert.True(session.Cut(run.TurnIndex, new TurnCut(string.Empty, TimeSpan.Zero)));
            await reading.WaitAsync(TimeSpan.FromSeconds(5), Ct);

            Task<TurnResult> next = session.RunTurnAsync(Next, Ct);
            await model.Holding.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            tool.Release.SetResult();
            await session.ToolRuns.WhenIdle();
            model.Release.SetResult();
            _ = await next;
            List<ChatMessage> nextRequest = model.Requests[^1];
            _ = await session.RunTurnAsync("thanks", Ct);

            Assert.DoesNotContain(nextRequest, message => message.Contents.OfType<FunctionResultContent>().Any());
            Assert.Equal([Question, Next, Answer, "call", "result", "thanks", Answer], Shapes(session.Transcript));
            Assert.Equal(1, tool.Runs);
        }

        // Order (c): the conversation ends while the tool runs. The end backstop stops it 30 s later; nothing is carried.
        [Fact(Timeout = 20_000)]
        public async Task AToolACutTurnLeftRunningIsStoppedThirtySecondsAfterTheEnd()
        {
            FakeTimeProvider time = new(Start);
            RecordingHook hook = new();
            GatedTool tool = new();
            ConversationSession session = Create(new NewestWordsToolChatClient("F63", Answer), tool, hook, time);

            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, Question), origin: null, Ct);
            Task reading = ReadAllAsync(run);
            await tool.Entered.Task;
            Assert.True(session.Cut(run.TurnIndex, new TurnCut(string.Empty, TimeSpan.Zero)));
            await reading.WaitAsync(TimeSpan.FromSeconds(5), Ct);

            _ = session.EndConversation(ConversationEndReason.CallerHungUp);
            await time.WaitForTimersAsync(Start + ConversationEnding.ToolGrace, 1).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            time.Advance(ConversationEnding.ToolGrace - TimeSpan.FromSeconds(1));
            Assert.False(tool.Cancelled.Task.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(1));
            await tool.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await session.ToolRuns.WhenIdle();
            await session.DisposeAsync();
            await session.FlushNoticesAsync();

            Assert.DoesNotContain(session.Transcript, message => message.Contents.OfType<FunctionResultContent>().Any());
            Assert.Equal(ToolOutcome.Failed, Assert.Single(hook.Of<ToolCalled>()).Outcome);
        }

        // A terminal stage ends the conversation as a host end does, so the same backstop stops the tool.
        [Fact(Timeout = 20_000)]
        public async Task AToolACutTurnLeftRunningIsStoppedThirtySecondsAfterATerminalStage()
        {
            FakeTimeProvider time = new(Start);
            GatedTool tool = new();
            ConversationSession session = HookSessions.Create(
                EndsAfterTheSecondTurnYaml,
                new NewestWordsToolChatClient("F63", Answer),
                tools: declared => tool.Create(declared.Id, declared.Description ?? declared.Id),
                time: time);

            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, Question), origin: null, Ct);
            Task reading = ReadAllAsync(run);
            await tool.Entered.Task;
            Assert.True(session.Cut(run.TurnIndex, new TurnCut(string.Empty, TimeSpan.Zero)));
            await reading.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.True((await session.RunTurnAsync(Next, Ct)).IsTerminal);

            await time.WaitForTimersAsync(time.GetUtcNow() + ConversationEnding.ToolGrace, 1).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            time.Advance(ConversationEnding.ToolGrace);
            await tool.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await session.ToolRuns.WhenIdle();
        }

        /// <summary>A store that refuses every append of words while <see cref="Refuses"/> is set, as when another host saved the turn first.</summary>
        private sealed class RefusingStore() : DelegatingConversationStore(new InMemoryConversationStore())
        {
            public bool Refuses { get; set; }

            public override ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
                string conversationId,
                IReadOnlyList<ConversationMessageDraft> messages,
                ConversationSessionState? state = null,
                CancellationToken cancellationToken = default)
            {
                return Refuses
                    ? throw new ConversationTurnConflictException("another host saved this turn first.")
                    : base.AppendAsync(conversationId, messages, state, cancellationToken);
            }
        }

        private static ConversationSession Create(IChatClient model, GatedTool tool, AgentHook? hook = null, TimeProvider? time = null)
        {
            return HookSessions.Create(
                HookSessions.ToolAgentYaml,
                model,
                hook is null ? [] : [hook],
                tools: declared => tool.Create(declared.Id, declared.Description ?? declared.Id),
                time: time);
        }

        private static List<string> Shapes(IEnumerable<ChatMessage> transcript)
        {
            return [
            .. transcript.Select(message => message.Contents switch
            {
                var contents when contents.OfType<FunctionCallContent>().Any() => "call",
                var contents when contents.OfType<FunctionResultContent>().Any() => "result",
                _ => message.Text,
            }),
        ];
        }

        private static Task ReadAllAsync(TurnRun run)
        {
            return Task.Run(
                async () =>
                {
                    await using (run)
                    {
                        await foreach (ChatResponseUpdate _ in run.Updates.WithCancellation(Ct))
                        {
                        }
                    }
                },
                Ct);
        }
    }
}

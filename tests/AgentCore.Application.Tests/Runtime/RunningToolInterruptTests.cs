using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Audit;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Hooks;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// A withdraw or a cut lets a running tool finish, keeps its
    /// call and result in the history of the next turn, and so never makes the tool run twice.
    /// </summary>
    public sealed class RunningToolInterruptTests
    {
        private const string Question = "Max speed of the F63?";

        private const string Combined = "Max speed of the F63? Oh wait, I meant the F80";

        private const string Answer = "ok";

        private const string Later = "and the F80?";

        private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        private const string StoppedAtEnd = "stopped 30 s after the conversation ended.";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // The GPT-Live correction: the older turn is cancelled, then resent on the same parent.
        [Fact(Timeout = 10_000)]
        public async Task ACorrectionWhileAToolRunsLetsItFinishOnceAndTheNewTurnReadsItsResult()
        {
            RecordingHook hook = new();
            GatedTool tool = new();
            CueToolChatClient model = new("F63", () => [], Answer);
            ConversationSession session = Create(model, tool, hook);
            _ = await session.RunTurnAtOriginAsync("hello", new ConversationTurnOrigin("d0", null) { NamesParent = true }, Ct);
            string anchor = session.LastReplyMessageId!;

            using CancellationTokenSource older = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            TurnRun first = await session.StartTurnAsync(
                new ChatMessage(ChatRole.User, Question), new ConversationTurnOrigin("d1", anchor) { NamesParent = true }, older.Token);
            Task reading = ReadAllAsync(first);
            await tool.Entered.Task;

            await older.CancelAsync();
            await EndedAsync(reading);
            tool.Release.SetResult();
            await session.ToolRuns.WhenIdle();

            TurnResult newer = await session.RunTurnAtOriginAsync(Combined, new ConversationTurnOrigin("d2", anchor) { NamesParent = true }, Ct);
            await session.FlushNoticesAsync();

            Assert.Equal((1, 1), (tool.Runs, tool.Finished));
            Assert.Equal(Answer, newer.ReplyText);
            Assert.Contains(model.Requests[^1], HasResult);
            Assert.Equal(["hello", Answer, Combined, "call", "result", Answer], Shapes(session.Transcript));
            _ = Assert.Single(hook.Of<TurnSuperseded>());
            Assert.Equal(ToolOutcome.Ok, Assert.Single(hook.Of<ToolCalled>()).Outcome);
        }

        // The Telnyx barge-in: a cut while the tool runs. The end-of-conversation backstop does not apply to a cut, and the
        // cut turn ends at once: the call and result ride the next turn.
        [Fact(Timeout = 10_000)]
        public async Task ACutWhileAToolRunsLetsItFinishAndKeepsItsCallAndResult()
        {
            FakeTimeProvider time = new(Start);
            GatedTool tool = new();
            CueToolChatClient model = new("F63", () => [], Answer);
            RecordingHook hook = new();
            ConversationSession session = Create(model, tool, hook, time: time);

            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, Question), origin: null, Ct);
            Task reading = ReadAllAsync(run);
            await tool.Entered.Task;

            Assert.True(session.Cut(run.TurnIndex, new TurnCut(string.Empty, TimeSpan.Zero)));
            await EndedAsync(reading);
            time.Advance(TimeSpan.FromMinutes(2));
            Assert.False(tool.Cancelled.Task.IsCompleted);
            tool.Release.SetResult();
            await session.ToolRuns.WhenIdle();
            _ = await session.RunTurnAsync(Later, Ct);

            Assert.Equal((1, 1), (tool.Runs, tool.Finished));
            Assert.Contains(model.Requests[^1], HasResult);
            Assert.Equal([Question, "call", "result", Later, Answer], Shapes(session.Transcript));
            await session.FlushNoticesAsync();
            Assert.Equal(ToolOutcome.Ok, Assert.Single(hook.Of<ToolCalled>()).Outcome);
        }

        // Only a call already running finishes: a later call of the same round never starts once the turn is cut.
        [Fact(Timeout = 10_000)]
        public async Task ACutLeavesTheRoundsLaterCallUnrun()
        {
            GatedTool tool = new();
            using ParallelToolChatClient model = new(Answer, ("price_lookup", "call_a"), ("price_lookup", "call_b"));
            ConversationSession session = Create(model, tool);

            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, Question), origin: null, Ct);
            Task reading = ReadAllAsync(run);
            await tool.Entered.Task;

            Assert.True(session.Cut(run.TurnIndex, new TurnCut(string.Empty, TimeSpan.Zero)));
            await EndedAsync(reading);
            tool.Release.SetResult();
            await session.ToolRuns.WhenIdle();
            _ = await session.RunTurnAsync(Combined, Ct);

            Assert.Equal((1, 1), (tool.Runs, tool.Finished));
            FunctionResultContent ran = Assert.Single(session.Transcript.SelectMany(message => message.Contents).OfType<FunctionResultContent>(), result => result.CallId == "call_a");
            Assert.Equal(GatedTool.Result, ran.Result?.ToString());
            Assert.DoesNotContain(
                session.Transcript.SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
                result => result.CallId == "call_b" && result.Result?.ToString() == GatedTool.Result);
        }

        // A tool still running when the conversation ends gets 30 s more, then is cancelled.
        [Fact(Timeout = 10_000)]
        public async Task AToolStillRunningWhenTheConversationEndsIsCancelledThirtySecondsLater()
        {
            FakeTimeProvider time = new(Start);
            GatedTool tool = new();
            CueToolChatClient model = new("F63", () => [], Answer);
            RecordingHook hook = new();
            ConversationSession session = Create(model, tool, hook, time: time);

            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, Question), origin: null, Ct);
            Task reading = ReadAllAsync(run);
            await tool.Entered.Task;

            _ = session.EndConversation(ConversationEndReason.CallerHungUp);
            await time.WaitForTimersAsync(Start + TimeSpan.FromSeconds(30), 1);
            time.Advance(TimeSpan.FromSeconds(29));
            Assert.False(tool.Cancelled.Task.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(1));
            await tool.Cancelled.Task;
            await EndedAsync(reading);

            await session.FlushNoticesAsync();

            Assert.Equal((1, 0), (tool.Runs, tool.Finished));
            Assert.DoesNotContain(session.Transcript, HasResult);
            ToolCalled stopped = Assert.Single(hook.Of<ToolCalled>());
            Assert.Equal((ToolOutcome.Failed, StoppedAtEnd), (stopped.Outcome, stopped.Failure));
        }

        // The stopped call is a tool.failed row of the audit chain, so the audit shows the call ran and was stopped.
        [Fact(Timeout = 10_000)]
        public async Task AToolStoppedThirtySecondsAfterTheEndIsAToolFailedRow()
        {
            FakeTimeProvider time = new(Start);
            GatedTool tool = new();
            InMemoryAuditSink sink = new();
            CueToolChatClient model = new("F63", () => [], Answer);
            ConversationSession session = ConversationSessionAuditTestSupport.Build(
                ConversationSessionAuditTestSupport.ToolYaml,
                model,
                fill: null,
                declared => tool.Create(declared.Id, declared.Description ?? declared.Id),
                time,
                sink).Create("conversation-1");

            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, Question), origin: null, Ct);
            Task reading = ReadAllAsync(run);
            await tool.Entered.Task;

            _ = session.EndConversation(ConversationEndReason.CallerHungUp);
            await time.WaitForTimersAsync(Start + TimeSpan.FromSeconds(30), 1);
            time.Advance(TimeSpan.FromSeconds(30));
            await tool.Cancelled.Task;
            await EndedAsync(reading);

            AuditEvent failed = Assert.Single(await session.RowsAsync(sink), row => row.Kind == AuditEventKind.ToolFailed);
            Assert.Equal(StoppedAtEnd, failed.Payload[AuditPayloadKeys.ToolError]);
        }

        [Fact(Timeout = 10_000)]
        public async Task AToolThatFinishesWithinThirtySecondsOfTheEndKeepsItsResult()
        {
            FakeTimeProvider time = new(Start);
            GatedTool tool = new();
            CueToolChatClient model = new("F63", () => [], Answer);
            ConversationSession session = Create(model, tool, time: time);

            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, Question), origin: null, Ct);
            Task reading = ReadAllAsync(run);
            await tool.Entered.Task;

            _ = session.EndConversation(ConversationEndReason.CallerHungUp);
            await time.WaitForTimersAsync(Start + TimeSpan.FromSeconds(30), 1);
            time.Advance(TimeSpan.FromSeconds(29));
            tool.Release.SetResult();
            await EndedAsync(reading);

            Assert.Equal((1, 1), (tool.Runs, tool.Finished));
            Assert.Contains(session.Transcript, HasResult);
        }

        // Host shutdown disposes the session, which waits for the running turn: the backstop bounds that wait too.
        [Fact(Timeout = 10_000)]
        public async Task AToolStillRunningWhenTheSessionIsDisposedIsCancelledThirtySecondsLater()
        {
            FakeTimeProvider time = new(Start);
            GatedTool tool = new();
            CueToolChatClient model = new("F63", () => [], Answer);
            ConversationSession session = Create(model, tool, time: time);

            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, Question), origin: null, Ct);
            Task reading = ReadAllAsync(run);
            await tool.Entered.Task;

            Task disposing = session.DisposeAsync().AsTask();
            await time.WaitForTimersAsync(Start + TimeSpan.FromSeconds(30), 1);
            time.Advance(TimeSpan.FromSeconds(29));
            Assert.False(tool.Cancelled.Task.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(1));
            await tool.Cancelled.Task;
            await EndedAsync(reading);
            await disposing;

            Assert.Equal((1, 0), (tool.Runs, tool.Finished));
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

        private static bool HasResult(ChatMessage message)
        {
            return message.Contents.OfType<FunctionResultContent>().Any(result => result.Result?.ToString() == GatedTool.Result);
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

        // A run the host cancelled ends in its cancellation; one whose model never read the token ends on its own.
        private static async Task EndedAsync(Task reading)
        {
            try
            {
                await reading;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static async Task ReadAllAsync(TurnRun run)
        {
            await using (run)
            {
                await foreach (ChatResponseUpdate _ in run.Updates)
                {
                }
            }
        }
    }
}

using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Hooks;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// Edit-and-resend withdraws a turn that is still running, so a caller's
    /// correction replaces the question it corrects and the model reads the caller's whole thought once.
    /// </summary>
    public sealed class RunningTurnSupersedeTests
    {
        private const string Question = "Max speed of the F63?";

        private const string Combined = "Max speed of the F63? Oh wait, I meant the F80";

        private const string Reply = "The F80 tops out at 10.5 mph.";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // The path PhoneCall takes: it cancels the older turn itself, then resends on the same parent.
        [Fact(Timeout = 10_000)]
        public async Task ACancelledRunningTurnResentOnItsParentIsWithdrawnAndTheModelReadsTheWordsOnce()
        {
            RecordingHook hook = new();
            using ScriptedChatClient scripted = new(Reply);
            GatedChatClient gated = new(scripted);
            RequestCapturingChatClient model = new(gated);
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, model, [hook]);
            _ = await session.RunTurnAtOriginAsync("hello", new ConversationTurnOrigin("d0", null) { NamesParent = true }, Ct);
            string anchor = session.LastReplyMessageId!;

            gated.Arm();
            using CancellationTokenSource older = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            TurnRun first = await session.StartTurnAsync(
                new ChatMessage(ChatRole.User, Question), new ConversationTurnOrigin("d1", anchor) { NamesParent = true }, older.Token);
            Task reading = ReadAllAsync(first);
            await gated.Entered.Task;

            await older.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
            gated.Open.SetResult();

            _ = await session.RunTurnAtOriginAsync(Combined, new ConversationTurnOrigin("d2", anchor) { NamesParent = true }, Ct);
            await session.FlushNoticesAsync();

            AssertTheCorrectionReplacedTheQuestion(model, session, hook, first.TurnIndex, supersededExpected: true);
        }

        // The engine on its own: a second start on the same parent waits the running turn out, then
        // withdraws it.
        [Fact(Timeout = 30_000)]
        public async Task ASecondStartOnTheSameParentWaitsTheRunningTurnOutThenWithdrawsIt()
        {
            RecordingHook hook = new();
            using ScriptedChatClient scripted = new(Reply);
            GatedChatClient gated = new(scripted);
            RequestCapturingChatClient model = new(gated);
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, model, [hook]);
            _ = await session.RunTurnAtOriginAsync("hello", new ConversationTurnOrigin("d0", null) { NamesParent = true }, Ct);
            string anchor = session.LastReplyMessageId!;

            gated.Arm();
            TurnRun first = await session.StartTurnAsync(
                new ChatMessage(ChatRole.User, Question), new ConversationTurnOrigin("d1", anchor) { NamesParent = true }, Ct);
            Task reading = ReadAllAsync(first);
            await gated.Entered.Task;

            Task<TurnResult> second = session.RunTurnAtOriginAsync(Combined, new ConversationTurnOrigin("d2", anchor) { NamesParent = true }, Ct);
            gated.Open.SetResult();
            await reading;
            _ = await second;
            await session.FlushNoticesAsync();

            AssertTheCorrectionReplacedTheQuestion(model, session, hook, first.TurnIndex, supersededExpected: true);
        }

        [AssertionMethod]
        private static void AssertTheCorrectionReplacedTheQuestion(
            RequestCapturingChatClient model, ConversationSession session, RecordingHook hook, int olderTurn, bool supersededExpected)
        {
            IReadOnlyList<ChatMessage> lastRequest = model.Requests[^1];
            Assert.Equal(1, lastRequest.Count(message => message.Role == ChatRole.User && message.Text == Combined));
            Assert.DoesNotContain(lastRequest, message => message.Text == Question);

            Assert.Equal(["hello", Reply, Combined, Reply], session.Transcript.Select(message => message.Text));

            if (supersededExpected)
            {
                TurnSuperseded superseded = Assert.Single(hook.Of<TurnSuperseded>());
                Assert.Equal((olderTurn, olderTurn), (superseded.WithdrewFrom, superseded.WithdrewThrough));
            }
            else
            {
                Assert.Empty(hook.Of<TurnSuperseded>());
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

using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Hooks;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Runtime
{
    // An edit that withdraws the turn that asked takes its open approval requests with it: nothing answers them.
    public sealed class ConversationSessionEditApprovalTests
    {
        private const string TwoToolsYaml =
            """
        apiVersion: agentcore/v1
        tools:
          - { id: send_email, kind: builtin, uses: test.send_email, description: "Send an email." }
          - { id: send_sms, kind: builtin, uses: test.send_sms, description: "Send a text message." }
        agents:
          items:
            - id: only
              instructions: "send both"
              tools: [ send_email, send_sms ]
        entries:
          main:
            agent: only
        """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AnEditOfATurnWhoseRequestsAreOpenLeavesNoAnswerWithoutItsRequest(bool underAnAutoRule)
        {
            string yaml = underAnAutoRule
                ? TwoToolsYaml.Replace("tools: [ send_email, send_sms ]", "tools: [ send_email, send_sms ]\n      approval: { auto: [ nothing_matches ] }", StringComparison.Ordinal)
                : TwoToolsYaml;
            int ran = 0;
            InMemoryConversationStore store = new();
            RecordingHook hook = new();
            ConversationSession session = HookSessions.Create(
                yaml,
                new TwoGatedCallsChatClient(),
                [hook],
                store: store,
                tools: declared => declared.Uses is "test.send_email" or "test.send_sms"
                    ? new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string to) => { ran++; return "sent"; }, declared.Id, declared.Id))
                    : null);

            TurnResult asked = await session.RunTurnAtOriginAsync("send both", new ConversationTurnOrigin("d1", null) { NamesParent = true }, Ct);
            Assert.NotEmpty(asked.Approvals);

            TurnResult edited = await session.RunTurnAtOriginAsync("send nothing", new ConversationTurnOrigin("d2", null) { NamesParent = true }, Ct);
            Assert.Null(edited.Failure);
            await session.FlushTranscriptAsync();

            HashSet<string> requested = [.. session.Transcript.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().Select(r => r.RequestId)];
            List<string> orphanAnswers = [.. session.Transcript.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>().Where(a => !requested.Contains(a.RequestId)).Select(a => a.RequestId)];

            TurnResult next = await session.RunTurnAsync("thanks", Ct);
            await session.FlushTranscriptAsync();
            await session.FlushNoticesAsync();
            await session.DisposeAsync();
            ConversationSession reloaded = HookSessions.Create(yaml, new TwoGatedCallsChatClient(), store: store, conversationId: session.ConversationId,
                tools: declared => declared.Uses is "test.send_email" or "test.send_sms"
                    ? new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string to) => { ran++; return "sent"; }, declared.Id, declared.Id))
                    : null);
            TurnResult afterReload = await reloaded.RunTurnAsync("still there?", Ct);
            Assert.Equal((0, (string?)null, (string?)null, 0), (orphanAnswers.Count, next.Failure, afterReload.Failure, ran));
            Assert.DoesNotContain(hook.Of<ApprovalChanged>(), changed => changed.State == ApprovalState.Denied && changed.Scope.TurnIndex == edited.TurnIndex);
        }

        // One of two requests was answered and its answer held for the other: the edit drops both, and the held answer,
        // so it never answers a later request. The fake model reuses its call ids, as a later round may.
        [Fact]
        public async Task AnEditDropsTheAnswerHeldForAWithdrawnRequest()
        {
            int ran = 0;
            ConversationSession session = HookSessions.Create(
                TwoToolsYaml,
                new QuietOnEditChatClient(),
                tools: declared => declared.Uses is "test.send_email" or "test.send_sms"
                    ? new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string to) => { ran++; return "sent"; }, declared.Id, declared.Id))
                    : null);

            TurnResult asked = await session.RunTurnAtOriginAsync("send both", new ConversationTurnOrigin("d1", null) { NamesParent = true }, Ct);
            _ = await session.RunTurnMessageAsync(session.TryCreateApprovalAnswer(asked.Approvals[0].RequestId, approved: true)!, Ct);
            _ = await session.RunTurnAtOriginAsync(QuietOnEditChatClient.Edit, new ConversationTurnOrigin("d2", null) { NamesParent = true }, Ct);
            TurnResult again = await session.RunTurnAsync("send both again", Ct);
            TurnResult half = await session.RunTurnMessageAsync(session.TryCreateApprovalAnswer(again.Approvals[1].RequestId, approved: true)!, Ct);

            Assert.Equal((0, asked.Approvals[0].RequestId), (ran, Assert.Single(half.Approvals).RequestId));
        }

        /// <summary>Calls both tools, except when the newest user words are the edit's, which it answers in words.</summary>
        private sealed class QuietOnEditChatClient() : DelegatingChatClient(new TwoGatedCallsChatClient())
        {
            public const string Edit = "send nothing";

            public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                List<ChatMessage> request = [.. messages];
                if (request.LastOrDefault(message => message.Role == ChatRole.User)?.Text == Edit)
                {
                    await Task.Yield();
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
                    yield break;
                }

                await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(request, options, cancellationToken))
                {
                    yield return update;
                }
            }
        }
    }
}

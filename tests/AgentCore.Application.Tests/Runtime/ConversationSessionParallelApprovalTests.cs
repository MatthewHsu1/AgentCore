using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Tests.Hooks;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// Two approval-required calls in one model round: one message may answer both, answers given one by one
    /// survive a reload of the conversation between them, and new words instead of an answer refuse what is open.
    /// </summary>
    public sealed class ConversationSessionParallelApprovalTests
    {
        /// <summary>What Microsoft.Extensions.AI tells the model about a call a person refused.</summary>
        private const string Rejected = "Tool call invocation rejected.";

        private const string MovedOn = Rejected + " the user moved on without answering.";

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

        private const string NoToolsYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - id: only
              instructions: "send both"
        entries:
          main:
            agent: only
        """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task OneMessageAnsweringBothRequests_RunsTheApprovedToolAndReplies()
        {
            Dictionary<string, int> ran = Ran();
            ConversationSession session = Session(TwoToolsYaml, ran, new InMemoryConversationStore());

            TurnResult asked = await session.RunTurnAsync("send both", Ct);
            Assert.Equal(2, asked.Approvals.Count);
            ChatMessage both = new(ChatRole.User, [
                .. session.TryCreateApprovalAnswer(RequestFor(asked, "send_email"), approved: true)!.Contents,
                .. session.TryCreateApprovalAnswer(RequestFor(asked, "send_sms"), approved: false)!.Contents,
            ]);
            TurnResult replied = await session.RunTurnMessageAsync(both, Ct);

            Assert.Null(replied.Failure);
            Assert.Empty(replied.Approvals);
            Assert.Equal(1, ran["send_email"]);
            Assert.Equal(0, ran["send_sms"]);
            Assert.Equal($"call_1=sent:send_email\ncall_2={Rejected}", replied.ReplyText);
        }

        // With an auto: block MAF's approval layer asks one request at a time and queues the other in its own state.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AnswersGivenOneByOne_SurviveAReloadBetweenThem(bool underAnAutoRule)
        {
            await AnswerOneByOneAcrossAReloadAsync(underAnAutoRule ? WithAutoRule(TwoToolsYaml) : TwoToolsYaml, hooks: null, expectedFirst: underAnAutoRule ? 1 : 2);
        }

        // A tool a hook adds at run time is no declared tool, yet it asks the same way.
        [Fact]
        public async Task ToolsAHookAddsAtRunTime_AreAnsweredOneByOneAcrossAReload()
        {
            Dictionary<string, int> ran = Ran();
            await AnswerOneByOneAcrossAReloadAsync(NoToolsYaml, [new AddingHook(ran)], expectedFirst: 2, ran);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task NewWordsWhileBothRequestsAreOpen_RefuseThemAndRunTheWords(bool underAnAutoRule)
        {
            Dictionary<string, int> ran = Ran();
            using RequestCapturingChatClient model = new(new TwoGatedCallsChatClient());
            ConversationSession session = Session(underAnAutoRule ? WithAutoRule(TwoToolsYaml) : TwoToolsYaml, ran, new InMemoryConversationStore(), model: model);

            _ = await session.RunTurnAsync("send both", Ct);
            TurnResult moved = await session.RunTurnAsync("never mind", Ct);

            Assert.Null(moved.Failure);
            Assert.Empty(moved.Approvals);
            Assert.Equal(0, ran["send_email"] + ran["send_sms"]);
            Assert.Equal($"call_1={MovedOn}\ncall_2={MovedOn}", moved.ReplyText);
            List<ChatMessage> seen = [.. model.Requests[^1]];
            int words = seen.FindLastIndex(message => message.Role == ChatRole.User);
            Assert.Equal("never mind", seen[words].Text);
            Assert.True(seen.FindLastIndex(message => message.Role == ChatRole.Tool) < words);
            Assert.Null((await session.RunTurnAsync("thanks", Ct)).Failure);
        }

        // An approval already given still counts when the caller moves on before answering the rest.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task NewWordsAfterOneAnswer_KeepThatAnswerAndRefuseTheRest(bool underAnAutoRule)
        {
            Dictionary<string, int> ran = Ran();
            RecordingHook hook = new();
            ConversationSession session = Session(underAnAutoRule ? WithAutoRule(TwoToolsYaml) : TwoToolsYaml, ran, new InMemoryConversationStore(), hooks: [hook]);

            TurnResult asked = await session.RunTurnAsync("send both", Ct);
            PendingApproval email = asked.Approvals.Single(approval => approval.ToolName == "send_email");
            _ = await session.RunTurnMessageAsync(session.TryCreateApprovalAnswer(email.RequestId, approved: true)!, Ct);
            TurnResult moved = await session.RunTurnAsync("never mind", Ct);

            Assert.Null(moved.Failure);
            Assert.Empty(moved.Approvals);
            Assert.Equal(1, ran["send_email"]);
            Assert.Equal(0, ran["send_sms"]);
            Assert.Equal($"call_1=sent:send_email\ncall_2={MovedOn}", moved.ReplyText);
            _ = await hook.WaitForAsync<TurnCompleted>(completed => completed.Scope.TurnIndex == 2).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(
                [("send_email", ApprovalState.Approved), ("send_sms", ApprovalState.Denied)],
                hook.Of<ApprovalChanged>().Where(changed => changed.State != ApprovalState.Asked).Select(changed => (changed.ToolName, changed.State)));
        }

        // A delegated agent runs without streaming: the same message reaches the model past MAF's queue.
        [Fact]
        public async Task UnderAnAutoRule_ARunThatDoesNotStream_TakesTheRefusalsAndTheWordsPastTheQueue()
        {
            Dictionary<string, int> ran = Ran();
            AIAgent agent = HookSessions.Compile(
                WithAutoRule(TwoToolsYaml),
                new TwoGatedCallsChatClient(),
                tools: declared => declared.Uses is "test.send_email" or "test.send_sms" ? Gated(declared.Id, ran) : null)["main"].Agents["only"];
            AgentSession session = await agent.CreateSessionAsync(Ct);

            AgentResponse asked = await agent.RunAsync("send both", session, cancellationToken: Ct);
            ToolApprovalRequestContent shown = Assert.Single(asked.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>());
            ToolApprovalRequestContent queued = Assert.Single(PendingApprovalQueue.Requests(session), request => request.RequestId != shown.RequestId);
            AgentResponse replied = await agent.RunAsync(
                TurnApprovalAnswers.MovedOn(new ChatMessage(ChatRole.User, "never mind"), [shown, queued]), session, cancellationToken: Ct);

            Assert.Equal(0, ran["send_email"] + ran["send_sms"]);
            Assert.Equal($"call_1={MovedOn}\ncall_2={MovedOn}", replied.Text);
        }

        private static async Task AnswerOneByOneAcrossAReloadAsync(
            string yaml, IReadOnlyList<AgentHook>? hooks, int expectedFirst, Dictionary<string, int>? ran = null)
        {
            ran ??= Ran();
            InMemoryConversationStore store = new();

            ConversationSession first = Session(yaml, ran, store, hooks: hooks);
            TurnResult asked = await first.RunTurnAsync("send both", Ct);
            Assert.Equal(expectedFirst, asked.Approvals.Count);
            string shown = asked.Approvals[0].ToolName;
            TurnResult waiting = await first.RunTurnMessageAsync(
                first.TryCreateApprovalAnswer(asked.Approvals[0].RequestId, approved: shown == "send_email")!, Ct);
            await first.FlushTranscriptAsync();

            PendingApproval rest = Assert.Single(waiting.Approvals);
            Assert.NotEqual(shown, rest.ToolName);

            ConversationSession resumed = Session(yaml, ran, store, first.ConversationId, hooks);
            ChatMessage? answer = await resumed.TryCreateApprovalAnswerAsync(rest.RequestId, approved: rest.ToolName == "send_email", Ct);
            Assert.NotNull(answer);
            TurnResult replied = await resumed.RunTurnMessageAsync(answer, Ct);

            Assert.Null(replied.Failure);
            Assert.Empty(replied.Approvals);
            Assert.Equal(1, ran["send_email"]);
            Assert.Equal(0, ran["send_sms"]);
            Assert.Equal($"call_1=sent:send_email\ncall_2={Rejected}", replied.ReplyText);
        }

        private static string WithAutoRule(string yaml)
        {
            return yaml.Replace("tools: [ send_email, send_sms ]", "tools: [ send_email, send_sms ]\n      approval: { auto: [ nothing_matches ] }", StringComparison.Ordinal);
        }

        private static Dictionary<string, int> Ran()
        {
            return new(StringComparer.Ordinal) { ["send_email"] = 0, ["send_sms"] = 0 };
        }

        private static string RequestFor(TurnResult asked, string tool)
        {
            return asked.Approvals.Single(approval => approval.ToolName == tool).RequestId;
        }

        private static ConversationSession Session(
            string yaml,
            Dictionary<string, int> ran,
            InMemoryConversationStore store,
            string? conversationId = null,
            IReadOnlyList<AgentHook>? hooks = null,
            IChatClient? model = null)
        {
            return HookSessions.Create(
                yaml,
                model ?? new TwoGatedCallsChatClient(),
                hooks,
                store,
                declared => declared.Uses is "test.send_email" or "test.send_sms" ? Gated(declared.Id, ran) : null,
                conversationId);
        }

        private static ApprovalRequiredAIFunction Gated(string name, Dictionary<string, int> ran)
        {
            return new(AIFunctionFactory.Create(
                (string to) =>
                {
                    ran[name]++;
                    return $"sent:{name}";
                },
                name,
                name));
        }

        /// <summary>Adds the two approval-required tools before the run, as a host's run-time tools would arrive.</summary>
        private sealed class AddingHook(Dictionary<string, int> ran) : AgentHook
        {
            public override ValueTask BeforeRunAsync(RunGate gate, CancellationToken cancellationToken)
            {
                gate.AddTools([Gated("send_email", ran), Gated("send_sms", ran)]);
                return ValueTask.CompletedTask;
            }
        }
    }
}

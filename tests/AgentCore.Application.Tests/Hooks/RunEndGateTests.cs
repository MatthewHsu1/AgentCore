using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class RunEndGateTests
    {
        private const string ContinueText = "CONTINUE-WITH-THIS";

        // Document: loop: re-runs until the todo list is empty, up to maxRounds.
        private const string TodoLoopYaml = """
        apiVersion: agentcore/v1
        agents:
          defaults: { clock: false }
          items:
            - { id: coder, instructions: "fix bugs", todos: true, loop: { maxRounds: 3, until: [{ todos: {} }] } }
        entries:
          main:
            agent: coder
        """;

        // The same loop with a cap of 4, so a run count of 4 can only come from maxRounds, never from the default 3.
        private const string FourRoundTodoLoopYaml = """
        apiVersion: agentcore/v1
        agents:
          defaults: { clock: false }
          items:
            - { id: coder, instructions: "fix bugs", todos: true, loop: { maxRounds: 4, until: [{ todos: {} }] } }
        entries:
          main:
            agent: coder
        """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // One user row, every run's assistant row, the continuation never stored, one TurnCompleted.
        [Fact]
        public async Task AContinueRunsAgainBelowTheSealAndStoresOneUserRow()
        {
            RecordingConversationStore store = new();
            RequestRecordingChatClient model = Answers();
            RecordingHook notices = new();
            ConversationSession session = HookSessions.Create(
                HookSessions.OneAgentYaml, model, [new Gate(gate => { if (gate.Iteration == 1) { gate.Continue(ContinueText); } }), notices], store);

            TurnResult turn = await session.RunTurnAsync("go", Ct);
            await session.FlushTranscriptAsync();
            await session.FlushNoticesAsync();

            Assert.Equal(2, model.Requests.Count);
            Assert.Equal($"user:{ContinueText}", model.Requests[1][^1]);
            IReadOnlyList<ConversationMessage> rows = store.Live(session.ConversationId);
            Assert.Equal("go", Assert.Single(rows, row => row.Content.Role == ChatRole.User).Content.Text);
            Assert.DoesNotContain(rows, row => row.Content.Text.Contains(ContinueText, StringComparison.Ordinal));
            Assert.Equal(2, rows.Count(row => row.Content.Role == ChatRole.Assistant));
            Assert.Equal("answer 2", turn.ReplyText);
            _ = Assert.Single(notices.Of<TurnCompleted>());
        }

        // A re-run reads the earlier run's reply again; the framework's tag on what it reads must not reach the stored row.
        [Fact]
        public async Task AContinuedRunStoresTheRepliesWithoutTheFrameworksReadTag()
        {
            RecordingConversationStore store = new();
            ConversationSession session = HookSessions.Create(
                HookSessions.OneAgentYaml,
                new VendorPropertyChatClient(Answers()),
                [new Gate(gate => { if (gate.Iteration == 1) { gate.Continue(ContinueText); } })],
                store);

            _ = await session.RunTurnAsync("go", Ct);
            await session.FlushTranscriptAsync();

            IReadOnlyList<ConversationMessage> replies = [.. store.Live(session.ConversationId).Where(row => row.Content.Role == ChatRole.Assistant)];
            Assert.Equal(2, replies.Count);
            Assert.All(replies, reply => Assert.Equal(["vendor.trace"], reply.Content.AdditionalProperties?.Keys ?? []));
        }

        // A re-run reads the conversation, this turn's user message and the
        // earlier run's reply, then the continuation. Nothing of it is stored (the test above).
        [Fact]
        public async Task AContinuedRunSeesTheTurnItContinues()
        {
            RequestRecordingChatClient model = Answers();
            ConversationSession session = HookSessions.Create(
                HookSessions.OneAgentYaml, model, [new Gate(gate => { if (gate.Scope.TurnIndex == 1 && gate.Iteration == 1) { gate.Continue(ContinueText); } })]);

            _ = await session.RunTurnAsync("first", Ct);
            _ = await session.RunTurnAsync("go", Ct);

            Assert.Equal(["user:first", "assistant:answer 1", "user:go", "assistant:answer 2", $"user:{ContinueText}"], model.Requests[2]);
        }

        // The first run of a turn reads the conversation and the caller's message once, with or without the gate.
        [Fact]
        public async Task ATurnThatRunsOnceReadsTheConversationOnce()
        {
            RequestRecordingChatClient model = Answers();
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, model, [new Gate(_ => { })]);

            _ = await session.RunTurnAsync("first", Ct);
            _ = await session.RunTurnAsync("go", Ct);

            Assert.Equal(2, model.Requests.Count);
            Assert.Equal(["user:first", "assistant:answer 1", "user:go"], model.Requests[1]);
        }

        // The same for loop:: the second run reads this turn's user message and the first run's work, then the feedback.
        [Fact]
        public async Task ADocumentLoopsSecondRunSeesTheTurnItContinues()
        {
            RequestCapturingChatClient capture = new(TodoAddingModel());
            ConversationSession session = HookSessions.Create(TodoLoopYaml, capture);

            _ = await session.RunTurnAsync("fix it", Ct);

            IReadOnlyList<ChatMessage> secondRun = capture.Requests[2];
            Assert.Equal(
                ["user:fix it", "assistant:FunctionCallContent", "tool:FunctionResultContent", "assistant:added"],
                secondRun.Take(4).Select(message => $"{message.Role}:{(message.Text.Length > 0 ? message.Text : message.Contents[0].GetType().Name)}"));
            Assert.StartsWith("You still have incomplete todo items.", secondRun[4].Text, StringComparison.Ordinal);
        }

        // ExcludeOnBehalfOfMessages keeps the framework's tag off the stored row.
        [Fact]
        public async Task TheContinuationNeverReachesTheCallersStream()
        {
            RequestRecordingChatClient model = Answers();
            ConversationSession session = HookSessions.Create(
                HookSessions.OneAgentYaml, model, [new Gate(gate => { if (gate.Iteration == 1) { gate.Continue(ContinueText); } })]);

            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("go", Ct))
            {
                updates.Add(update);
            }

            Assert.Equal(2, model.Requests.Count);
            Assert.DoesNotContain(updates, update => update.Text.Contains(ContinueText, StringComparison.Ordinal));
            Assert.DoesNotContain(updates, update => update.Role == ChatRole.User);
        }

        // With no loop:, the cap is 3 runs.
        [Fact]
        public async Task ContinueIsCappedAtThreeRunsWithoutALoopBlock()
        {
            RequestRecordingChatClient model = Answers();
            ConversationSession session = HookSessions.Create(HookSessions.OneAgentYaml, model, [new Gate(gate => gate.Continue("again"))]);

            _ = await session.RunTurnAsync("go", Ct);

            Assert.Equal(3, model.Requests.Count);
        }

        // The same tag leak applies to loop: itself, and the same fix covers it.
        [Fact]
        public async Task ADocumentLoopNoLongerLeaksItsFeedbackToTheStream()
        {
            ToolCallingChatClient model = TodoAddingModel();
            ConversationSession session = HookSessions.Create(TodoLoopYaml, model);

            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("fix it", Ct))
            {
                updates.Add(update);
            }

            Assert.Equal(6, model.Calls);
            Assert.DoesNotContain(updates, update => update.Role == ChatRole.User);
        }

        // The document's until: decides first. An open todo re-runs the loop with the todo feedback
        // every time, up to maxRounds (3 runs x 2 rounds), so the hook, which goes last, never continues.
        [Fact]
        public async Task TheDocumentsUntilDecidesFirst()
        {
            ToolCallingChatClient model = TodoAddingModel();
            RequestCapturingChatClient capture = new(model);
            ConversationSession session = HookSessions.Create(TodoLoopYaml, capture, [new Gate(gate => gate.Continue(ContinueText))]);

            _ = await session.RunTurnAsync("fix it", Ct);

            Assert.Equal(6, model.Calls);
            Assert.DoesNotContain(capture.Requests, request => request.Any(message => message.Text == ContinueText));
        }

        // With no open todo the document's until: stops, and the hook's Continue runs again up to maxRounds (4), not 3.
        [Fact]
        public async Task AHookContinuesAfterTheDocumentLoopStopsUpToMaxRounds()
        {
            RequestRecordingChatClient model = Answers();
            ConversationSession session = HookSessions.Create(FourRoundTodoLoopYaml, model, [new Gate(gate => gate.Continue(ContinueText))]);

            _ = await session.RunTurnAsync("go", Ct);

            Assert.Equal(4, model.Requests.Count);
            Assert.All(model.Requests.Skip(1), request => Assert.Contains($"user:{ContinueText}", request));
        }

        // The run-end gate fires for the turn's own run, never for an agent-as-tool child.
        [Fact]
        public async Task AnAgentToolChildNeverFiresTheGate()
        {
            List<string?> seen = [];
            ToolCallingChatClient model = new("done", new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "help me" });
            ConversationSession session = HookSessions.Create(HookSessions.DelegatingYaml, model, [new Gate(gate => seen.Add(gate.AgentId))]);

            _ = await session.RunTurnAsync("go", Ct);

            Assert.Equal(["ask_helper"], model.Called);
            Assert.Equal(["only"], seen);
        }

        // A Closed run-end hook that fails stops the chain, and the turn does not run again.
        [Fact]
        public async Task AClosedHookThatFailsEndsTheTurnWithoutRunningAgain()
        {
            RequestRecordingChatClient model = Answers();
            RecordingHook notices = new();
            ConversationSession session = HookSessions.Create(
                HookSessions.OneAgentYaml, model, [new ClosedThrowingGate(), new Gate(gate => gate.Continue(ContinueText)), notices]);

            TurnResult turn = await session.RunTurnAsync("go", Ct);
            await session.FlushNoticesAsync();

            _ = Assert.Single(model.Requests);
            Assert.Equal("answer 1", turn.ReplyText);
            Assert.Equal(FaultKind.HookFailed, Assert.Single(notices.Of<Fault>()).Kind);
        }

        // LoopAgent hands every run the same options, so no layer may stack across runs.
        [Fact]
        public async Task AContinuedRunIsOneToolNoticePerCallAndOneModelNoticePerRound()
        {
            RecordingHook notices = new();
            ToolCallingChatClient model = new("final", everyRun: true);
            ConversationSession session = HookSessions.Create(
                HookSessions.ToolAgentYaml,
                model,
                [new Gate(gate => { if (gate.Iteration == 1) { gate.Continue(ContinueText); } }), notices],
                tools: new StubToolBuilder("{}").Create);

            _ = await session.RunTurnAsync("how much?", Ct);
            await session.FlushNoticesAsync();

            // Each of the two runs is a tool round and an answer round.
            Assert.Equal(ContinueText, model.Prompts[^1]);
            Assert.Equal(["price_lookup", "price_lookup"], model.Called);
            Assert.Equal(["price_lookup", "price_lookup"], notices.Of<ToolCalled>().Select(called => called.ToolName));
            Assert.Equal(4, notices.Of<ModelCalled>().Count);
        }

        private static RequestRecordingChatClient Answers()
        {
            return new("answer 1", "answer 2", "answer 3", "answer 4");
        }

        // Adds a todo on every run, so the todo list never empties and until: todos always asks for another run.
        private static ToolCallingChatClient TodoAddingModel()
        {
            return new(
            "added",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["todos"] = new object[] { new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = "T" } },
            },
            everyRun: true);
        }

        /// <summary>Stamps a vendor property on every update, so each reply message carries a property bag of its own.</summary>
        private sealed class VendorPropertyChatClient(IChatClient inner) : DelegatingChatClient(inner)
        {
            public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
                {
                    update.AdditionalProperties = new() { ["vendor.trace"] = "t1" };
                    yield return update;
                }
            }
        }

        private sealed class Gate(Action<RunEndGate> decide) : AgentHook
        {
            public override ValueTask AfterRunAsync(RunEndGate gate, CancellationToken cancellationToken)
            {
                decide(gate);
                return default;
            }
        }

        private sealed class ClosedThrowingGate : AgentHook
        {
            public override HookFailure FailureFor(GatePoint point)
            {
                return HookFailure.Closed;
            }

            public override ValueTask AfterRunAsync(RunEndGate gate, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("broken");
            }
        }
    }
}

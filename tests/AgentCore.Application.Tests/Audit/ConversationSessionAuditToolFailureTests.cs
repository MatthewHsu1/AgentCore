using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Audit.ConversationSessionAuditTestSupport;

namespace AgentCore.Application.Tests.Audit
{
    /// <summary>The tool.failed rows: one per call whose fault the model cannot answer, and one per tool it named that no one declared.</summary>
    public sealed class ConversationSessionAuditToolFailureTests
    {
        [Fact]
        public async Task TheFourFailedCallsAreFourToolFailedRowsBeforeTheTurnEvent()
        {
            using LoopingToolCallingChatClient reply = new();
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(ToolYaml, reply, null, new ThrowingToolBuilder().Create, auditSink: sink).Create("conversation-1");

            _ = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            IReadOnlyList<AuditEvent> events = await session.RowsAsync(sink);
            AuditEvent[] failures = [.. events.Where(item => item.Kind == AuditEventKind.ToolFailed)];
            AuditEvent completed = Assert.Single(events, item => item.Kind == AuditEventKind.TurnCompleted);

            // One row per failed call, and every one of them before the turn event.
            Assert.Equal(4, failures.Length);
            List<AuditEvent> order = [.. events];
            Assert.All(failures, failure => Assert.True(order.IndexOf(failure) < order.IndexOf(completed)));
            Assert.All(failures, failure => Assert.Equal(0, failure.TurnIndex));

            // Each row names its tool. The fourth is the one that spent the budget: the framework does not
            // capture that exception at all, so it never reaches CreateResponseMessages, and only the tool
            // middleware sees it.
            Assert.All(failures, failure => Assert.Equal("lookup_order", failure.Payload[AuditPayloadKeys.ToolName]));
            Assert.All(
                failures,
                failure => Assert.Equal(
                    ToolFailureKinds.ToToken(ToolFailureKind.Faulted),
                    failure.Payload[AuditPayloadKeys.ToolFailureKind]));
            Assert.All(
                failures,
                failure => Assert.Contains(
                    ThrowingToolBuilder.Message,
                    failure.Payload[AuditPayloadKeys.ToolError],
                    StringComparison.Ordinal));

            // Four calls, four ids, and none of them repeated. The name alone could never have told them
            // apart.
            string[] ids = [.. failures.Select(failure => failure.Payload[AuditPayloadKeys.ToolCallId])];
            Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());

            // The chain still verifies over every one of these keys.
            Assert.All(events, AuditEventVocabulary.Validate);
        }

        // An agent-as-tool whose child spends its budget faults too, so the child's calls and the agent tool's own
        // call are each a row: 4 delegations of 4 child calls.
        [Fact]
        public async Task ANestedFaultIsARowForEveryChildCallAndForEveryAgentToolCall()
        {
            const string NestedYaml = """
            apiVersion: agentcore/v1
            tools:
              - { id: ask_specialist, kind: agent, agent: specialist, description: Ask the specialist. }
              - { id: lookup_order, kind: builtin, uses: orders.read, description: "Look up an order by its id." }
            agents:
              defaults:
                model: { ref: reply }
              items:
                - { id: greeter, instructions: "greet", tools: [ ask_specialist ] }
                - { id: specialist, instructions: "answer", tools: [ lookup_order ] }
            entries:
              main:
                agent: greeter
            """;
            using LoopingToolCallingChatClient reply = new();
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(NestedYaml, reply, null, new ThrowingToolBuilder().Create, auditSink: sink).Create("conversation-1");

            _ = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            AuditEvent[] failures = [.. (await session.RowsAsync(sink)).Where(item => item.Kind == AuditEventKind.ToolFailed)];
            Assert.Equal(
                [("ask_specialist", 4), ("lookup_order", 16)],
                failures.GroupBy(failure => failure.Payload[AuditPayloadKeys.ToolName]).Select(group => (group.Key, group.Count())).Order());
            Assert.Equal(20, failures.Select(failure => failure.Payload[AuditPayloadKeys.ToolCallId]).Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public async Task AToolThatFaults_NamesTheToolAndTheConversationIdInTheChain()
        {
            // One failing round, then the model answers, so the turn ends normally and the only thing
            // that could ever have recorded the tool is the observer.
            using NamedToolCallingChatClient reply = new("lookup_order", "I could not reach the order system.");
            InMemoryAuditSink sink = new();
            UnreachableEndpointToolBuilder tools = new();
            ConversationSession session = Build(ToolYaml, reply, null, tools.Create, auditSink: sink).Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            AuditEvent failed = Assert.Single(await session.RowsAsync(sink), item => item.Kind == AuditEventKind.ToolFailed);
            Assert.Equal("lookup_order", failed.Payload[AuditPayloadKeys.ToolName]);
            Assert.Equal(reply.CallIds[0], failed.Payload[AuditPayloadKeys.ToolCallId]);
            Assert.Equal(
                ToolFailureKinds.ToToken(ToolFailureKind.Faulted),
                failed.Payload[AuditPayloadKeys.ToolFailureKind]);
            Assert.Contains(
                UnreachableEndpointToolBuilder.Message,
                failed.Payload[AuditPayloadKeys.ToolError],
                StringComparison.Ordinal);

            // One failure is under the budget, so the turn is an ordinary turn that spoke.
            Assert.Null(turn.Failure);
            Assert.Equal(0, failed.TurnIndex);
        }

        [Fact]
        public async Task AHallucinatedToolName_ReachesTheChain()
        {
            // The model invented the name. The framework answers it with a message and NO exception, so
            // it spends none of the error budget and the turn goes on — which is correct, and is exactly
            // why nothing else in the system would ever have recorded that it happened.
            using NamedToolCallingChatClient reply = new("lookup_ordar", "Let me try that again.");
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(ToolYaml, reply, null, new StubToolBuilder(/*lang=json,strict*/ """{"status":"shipped"}""").Create, auditSink: sink)
                .Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            AuditEvent failed = Assert.Single(await session.RowsAsync(sink), item => item.Kind == AuditEventKind.ToolFailed);

            // The name recorded is the name the MODEL called, so a reader that joins it to tools[].id
            // finds nothing — which is the finding.
            Assert.Equal("lookup_ordar", failed.Payload[AuditPayloadKeys.ToolName]);
            Assert.Equal(reply.CallIds[0], failed.Payload[AuditPayloadKeys.ToolCallId]);
            Assert.Equal(
                ToolFailureKinds.ToToken(ToolFailureKind.Undeclared),
                failed.Payload[AuditPayloadKeys.ToolFailureKind]);

            // The turn is unharmed: no exception, no budget spent, and the caller heard a real reply.
            Assert.Null(turn.Failure);
            Assert.Equal("Let me try that again.", turn.ReplyText);
        }

        [Fact]
        public async Task TwoParallelCallsToTheSameTool_AreTwoDistinguishableRecords()
        {
            // One assistant message, two calls, one tool name. Without the call id these are one fact
            // written twice, and a reader cannot tell which call failed.
            using NamedToolCallingChatClient reply = new("lookup_order", "Both lookups failed.", callsPerTurn: 2);
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(ToolYaml, reply, null, new UnreachableEndpointToolBuilder().Create, auditSink: sink)
                .Create("conversation-1");

            _ = await session.RunTurnAsync("where are my two orders", TestContext.Current.CancellationToken);

            AuditEvent[] failures = [.. (await session.RowsAsync(sink)).Where(item => item.Kind == AuditEventKind.ToolFailed)];

            Assert.Equal(2, failures.Length);
            Assert.All(failures, failure => Assert.Equal("lookup_order", failure.Payload[AuditPayloadKeys.ToolName]));
            Assert.Equal(
                reply.CallIds.ToArray(),
                failures.Select(failure => failure.Payload[AuditPayloadKeys.ToolCallId]).Order(StringComparer.Ordinal).ToArray());

            // Two records, and the chain still verifies over them.
            Assert.All(await session.RowsAsync(sink), AuditEventVocabulary.Validate);
        }

        [Fact]
        public async Task AToolWhoseFaultTheModelCanAnswer_WritesNoToolFailedEventAtAll()
        {
            // From the chain's side: the tool ANSWERED. The framework sees a result
            // and no exception, so nothing failed as far as it is concerned and no row is written.
            using NamedToolCallingChatClient reply = new("lookup_order", "That order is already closed.");
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(ToolYaml, reply, null, new RefusedRequestToolBuilder().Create, auditSink: sink)
                .Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            Assert.DoesNotContain(await session.RowsAsync(sink), item => item.Kind == AuditEventKind.ToolFailed);
            Assert.Null(turn.Failure);
            Assert.Equal("That order is already closed.", turn.ReplyText);
        }

        /// <summary>One compiled agent serves every conversation, so nothing per conversation may live on it.</summary>
        [Fact]
        public async Task TwoConversationsAtOnce_DoNotPolluteEachOthersRecords()
        {
            const int FanOut = 8;
            InMemoryAuditSink sink = new();

            // One factory, one document, one compiled agent behind the sessions.
            using NamedToolCallingChatClient reply = new("lookup_order", "I could not reach the order system.");
            ConversationSessionFactory factory = Build(ToolYaml, reply, null, new UnreachableEndpointToolBuilder().Create, auditSink: sink);

            CancellationToken token = TestContext.Current.CancellationToken;
            using Barrier gate = new(FanOut);

            ConversationSession[] sessions = [.. Enumerable.Range(0, FanOut).Select(index => factory.Create($"conversation-{index}"))];

            await Task.WhenAll(Enumerable.Range(0, FanOut).Select(index => Task.Run(
                async () =>
                {
                    ConversationSession session = sessions[index];
                    gate.SignalAndWait(token);
                    _ = await session.RunTurnAsync("where is my order", token).ConfigureAwait(false);
                },
                token)));

            for (int index = 0; index < FanOut; index++)
            {
                IReadOnlyList<AuditEvent> events = await sessions[index].RowsAsync(sink);

                // Each conversation recorded exactly its own failure, under its own id, and its three events kept
                // their own order. A record that had leaked between two flows would show up as a second
                // tool.failed here or as one of these kinds out of place.
                AuditEvent failed = Assert.Single(events, item => item.Kind == AuditEventKind.ToolFailed);
                Assert.Equal("lookup_order", failed.Payload[AuditPayloadKeys.ToolName]);
                Assert.NotEmpty(failed.Payload[AuditPayloadKeys.ToolCallId]);
                Assert.Equal(
                    [AuditEventKind.ConversationStarted, AuditEventKind.ToolFailed, AuditEventKind.TurnCompleted],
                    events.Select(item => item.Kind).ToArray());
                Assert.All(events, AuditEventVocabulary.Validate);
            }
        }
    }
}

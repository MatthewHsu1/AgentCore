using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Audit
{
    /// <summary>
    /// <see cref="AuditPayloadKeys.ReplyTextSha256"/> covers the whole reply, not only its last step.
    /// </summary>
    public sealed class ConversationSessionMultiStepAuditEventsTests
    {
        [Fact]
        public async Task ASingleStepTurn_HashesTheOneStepItSpoke()
        {
            using SequencedChatClient reply = new("hello there.");
            InMemoryAuditSink sink = new();
            ConversationSession session = InterruptionSessions.CreateSession(
                InterruptionSessions.NoToolYaml, reply, auditSink: sink);

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            AuditEvent completed = Assert.Single(
                await session.RowsAsync(sink), item => item.Kind == AuditEventKind.TurnCompleted);
            Assert.Equal(
                "92ea4f7debacecc2bb655776bd3406477c3c75d2575d40e3d9d67e763b5d5cec",
                completed.Payload[AuditPayloadKeys.ReplyTextSha256]);
        }

        [Fact]
        public async Task AMultiStepTurn_HashesEveryStepInOrder()
        {
            // The model announces the lookup in prose on the same message as its call, then answers once
            // the result is in. The hash covers both steps, not only "the price is fifty".
            ProseBesideToolChatClient client = new("the price is fifty");
            client.OpenGate();
            InMemoryAuditSink sink = new();
            ConversationSession session = InterruptionSessions.CreateSession(
                InterruptionSessions.ToolYaml,
                client,
                new StubToolBuilder(/*lang=json,strict*/ """{ "price": 50 }""").Create,
                sink);

            _ = await session.RunTurnAsync("what does it cost", TestContext.Current.CancellationToken);

            AuditEvent completed = Assert.Single(
                await session.RowsAsync(sink), item => item.Kind == AuditEventKind.TurnCompleted);
            Assert.Equal(
                "0aeee3157d498f34407043e72714af88cbb07c27dc76050b8a294e7b96a6df9b",
                completed.Payload[AuditPayloadKeys.ReplyTextSha256]);
        }

        [Fact]
        public async Task ACutMultiStepTurn_HashesTheGeneratedReplyAndTheHeardTextSeparately()
        {
            // The barge-in is reported after the turn already ran to completion, so the
            // model's own output is fixed and deterministic: the whole two-step reply. The relay's report
            // of what the caller heard is a separate, independent value. turn.completed proves the first;
            // reply.interrupted proves the second, and the two must not be conflated into one hash.
            ProseBesideToolChatClient client = new("the total comes to eighty");
            client.OpenGate();
            InMemoryAuditSink sink = new();
            ConversationSession session = InterruptionSessions.CreateSession(
                InterruptionSessions.ToolYaml,
                client,
                new StubToolBuilder(/*lang=json,strict*/ """{ "price": 50 }""").Create,
                sink);

            _ = await session.RunTurnAsync("what does it cost", TestContext.Current.CancellationToken);
            Assert.True(session.Cut(
                0, new TurnCut(ProseBesideToolChatClient.Prose + "the total", TimeSpan.FromMilliseconds(1820))));

            IReadOnlyList<AuditEvent> events = await session.RowsAsync(sink);
            AuditEvent completed = Assert.Single(events, item => item.Kind == AuditEventKind.TurnCompleted);
            AuditEvent interrupted = Assert.Single(events, item => item.Kind == AuditEventKind.ReplyInterrupted);

            Assert.Equal(
                "2321bd05672f296a576e169190756dce4be21568a9d3d0542d45375ad056a7d8",
                completed.Payload[AuditPayloadKeys.ReplyTextSha256]);
            Assert.Equal(
                "5b79609c62265cd562cd5e43a658f62e704bf8a3342810fd576640da7f25591a",
                interrupted.Payload[AuditPayloadKeys.UtteranceUntilInterruptSha256]);
        }

        [Fact]
        public async Task AToolThatSpendsItsBudgetAfterTheModelSpoke_HashesTheStepsAndTheFallbackTheRowsHold()
        {
            ProseThenFailingToolChatClient client = new();
            InMemoryAuditSink sink = new();
            ConversationSession session = InterruptionSessions.CreateSession(
                InterruptionSessions.ToolYaml, client, new ThrowingToolBuilder().Create, sink);

            TurnResult turn = await session.RunTurnAsync("what does it cost", TestContext.Current.CancellationToken);

            Assert.NotNull(turn.Failure);
            Assert.Equal(
                "Checking 1. Checking 2. Checking 3. Checking 4.I am sorry. I could not finish that. Please say it again.",
                await SpokenAsync(session, turnIndex: 0));
            AuditEvent completed = Assert.Single(
                await session.RowsAsync(sink), item => item.Kind == AuditEventKind.TurnCompleted);
            Assert.Equal(
                "40e79eac3b0c25fe5d27e0fd6e0ee02aecc48ff93b81af341e0ec3cdb875812d",
                completed.Payload[AuditPayloadKeys.ReplyTextSha256]);
        }

        [Fact]
        public async Task AReaderThatLeavesDuringTheSecondStep_HashesEveryStepItWasShown()
        {
            ProseBesideToolChatClient client = new("the price is fifty");
            InMemoryAuditSink sink = new();
            ConversationSession session = InterruptionSessions.CreateSession(
                InterruptionSessions.ToolYaml,
                client,
                new StubToolBuilder(/*lang=json,strict*/ """{ "price": 50 }""").Create,
                sink);

            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("what does it cost", TestContext.Current.CancellationToken))
            {
                if (update.Text == "the")
                {
                    break;
                }
            }

            Assert.Equal("Let me check that for youthe", await SpokenAsync(session, turnIndex: 0));
            Assert.Equal("Let me check that for youthe", session.LastTurn?.ReplyText);
            AuditEvent interrupted = Assert.Single(
                await session.RowsAsync(sink), item => item.Kind == AuditEventKind.ReplyInterrupted);
            Assert.Equal(
                "7b1485a90a236eb1d3a77c433b1e9371d93481283ab337b76b59c3c059bc1d51",
                interrupted.Payload[AuditPayloadKeys.UtteranceUntilInterruptSha256]);
        }

        [Fact]
        public async Task ALateCutThatNamesNoText_KeepsEveryStepAndHashesThem()
        {
            ProseBesideToolChatClient client = new("the total comes to eighty");
            client.OpenGate();
            InMemoryAuditSink sink = new();
            ConversationSession session = InterruptionSessions.CreateSession(
                InterruptionSessions.ToolYaml,
                client,
                new StubToolBuilder(/*lang=json,strict*/ """{ "price": 50 }""").Create,
                sink);

            _ = await session.RunTurnAsync("what does it cost", TestContext.Current.CancellationToken);
            Assert.True(session.Cut(0, new TurnCut(ShownText: null, TimeSpan.FromMilliseconds(900))));

            Assert.Equal("Let me check that for youthe total comes to eighty", await SpokenAsync(session, turnIndex: 0));
            AuditEvent interrupted = Assert.Single(
                await session.RowsAsync(sink), item => item.Kind == AuditEventKind.ReplyInterrupted);
            Assert.Equal(
                "2321bd05672f296a576e169190756dce4be21568a9d3d0542d45375ad056a7d8",
                interrupted.Payload[AuditPayloadKeys.UtteranceUntilInterruptSha256]);
        }

        /// <summary>
        /// Rebuilds one turn's words the way the store's verify query does: the text parts of every assistant row
        /// that carries one, in ordinal order, with no separator.
        /// </summary>
        private static async Task<string> SpokenAsync(ConversationSession session, int turnIndex)
        {
            await session.FlushTranscriptAsync();
            IReadOnlyList<ConversationMessage> rows = await session.Compiled.ConversationStore
                .ReadForSessionAsync(session.ConversationId, TestContext.Current.CancellationToken);

            return string.Concat(rows
                .Where(row => row.TurnIndex == turnIndex && row.CoversUpTo is null && row.Content.Role == ChatRole.Assistant)
                .OrderBy(row => row.Ordinal)
                .SelectMany(row => row.Content.Contents.OfType<TextContent>())
                .Select(text => text.Text));
        }
    }
}

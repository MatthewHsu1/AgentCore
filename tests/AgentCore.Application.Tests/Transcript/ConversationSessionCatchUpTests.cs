using System.Text.Json.Nodes;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Transcript.ConversationSessionResumeTestSupport;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>A long-lived session that catches up on a turn another session of its conversation saved.</summary>
    public sealed class ConversationSessionCatchUpTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private const string OrderYaml = """
          apiVersion: agentcore/v1
          state:
            orderStatus: { type: string, writer: extractor }
            turnsInAlpha:
              type: integer
              default: 0
              writer: counter
              increment: { "===": [ { var: stage }, "alpha" ] }
            turnsInBeta:
              type: integer
              default: 0
              writer: counter
              increment: { "===": [ { var: stage }, "beta" ] }
          guards:
            shipped: { "===": [ { var: orderStatus }, "shipped" ] }
          agents:
            defaults: { clock: false }
            items:
              - { id: alpha, instructions: "STAGE-ALPHA" }
              - { id: beta, instructions: "STAGE-BETA" }
              - { id: done, instructions: "STAGE-DONE" }
          entries:
            main:
              policy:
                initial: alpha
                stages:
                  - { id: alpha, agent: alpha, to: [ { stage: beta, when: shipped } ] }
                  - { id: beta, agent: beta }
                  - { id: closed, agent: done, terminal: true }
          """;

        // IConversationStore.AppendAsync refuses a turn index the conversation already saved. Session B saved turn 1, so session A, which ran turn 0, must run its next turn as turn 2.
        [Fact]
        public async Task ASessionThatCaughtUpOnAnotherSessionsTurn_SavesItsNextTurnUnderTheNextIndex()
        {
            InMemoryConversationStore store = new();
            using ScriptedChatClient replyA = new("a");
            ConversationSession a = CreateSession(OneAgentYaml, replyA, store);
            _ = await a.RunTurnAsync("q0", Ct);
            await a.FlushTranscriptAsync();

            using ScriptedChatClient replyB = new("b");
            ConversationSession b = CreateSession(OneAgentYaml, replyB, store, a.ConversationId);
            _ = await b.RunTurnAsync("q1", Ct);
            await b.FlushTranscriptAsync();

            TurnResult third = await a.RunTurnAsync("q2", Ct);
            await a.FlushTranscriptAsync();

            Assert.Equal(2, third.TurnIndex);
            IReadOnlyList<ConversationMessage> rows = await store.ReadForSessionAsync(a.ConversationId, Ct);
            Assert.Equal(
                [(0, "q0"), (0, "a"), (1, "q1"), (1, "b"), (2, "q2"), (2, "a")],
                rows.Select(row => (row.TurnIndex, row.Content.Text)));
        }

        [Fact]
        public async Task ASessionThatCaughtUpOnAnotherSessionsTurn_RunsInTheStageAndSeesTheSlotsThatTurnStored()
        {
            InMemoryConversationStore store = new();
            using RequestRecordingChatClient reply = new("a0", "a2");
            ConversationSession a = CreateSession(OrderYaml, reply, store);
            _ = await a.RunTurnAsync("q0", Ct);
            await a.FlushTranscriptAsync();
            await SaveAnotherSessionsTurnAsync(store, a.ConversationId, new ConversationSessionState
            {
                NextTurnIndex = 2,
                Stage = "beta",
                Slots = new Dictionary<string, JsonNode?> { ["orderStatus"] = "shipped", ["turnsInAlpha"] = 2L },
            });

            TurnResult third = await a.RunTurnAsync("q2", Ct);
            await a.FlushTranscriptAsync();

            Assert.Equal((2, "beta"), (third.TurnIndex, third.StageBefore));
            Assert.EndsWith("STAGE-BETA", reply.Instructions[^1], StringComparison.Ordinal);
            ConversationSessionState? stored = (await store.GetAsync(a.ConversationId, Ct))?.State;
            Assert.Equal(("beta", 3), (stored?.Stage, stored?.NextTurnIndex));
            Assert.Equal(
                [("orderStatus", "shipped"), ("turnsInAlpha", "2"), ("turnsInBeta", "1")],
                stored!.Slots.OrderBy(slot => slot.Key, StringComparer.Ordinal).Select(slot => (slot.Key, slot.Value?.ToString())));
        }

        // The stored state is the conversation's: a slot only this session holds, say from a turn the store refused,
        // is not part of it.
        [Fact]
        public async Task ASessionThatCaughtUpOnAnotherSessionsTurn_HoldsNoSlotThatTurnDidNotStore()
        {
            InMemoryConversationStore store = new();
            using ScriptedChatClient reply = new("a");
            ConversationSession a = CreateSession(OrderYaml, reply, store);
            _ = await a.RunTurnAsync("q0", Ct);
            await a.FlushTranscriptAsync();
            await SaveAnotherSessionsTurnAsync(store, a.ConversationId, new ConversationSessionState { NextTurnIndex = 2, Stage = "alpha" });

            _ = await a.RunTurnAsync("q2", Ct);

            Assert.Equal(1, a.State.Read("turnsInAlpha")?.GetValue<long>());
        }

        [Fact]
        public async Task ASessionThatCaughtUpOnAnotherSessionsTurn_RefusesTheTurnWhenThatTurnEndedTheConversation()
        {
            InMemoryConversationStore store = new();
            using ScriptedChatClient reply = new("a");
            ConversationSession a = CreateSession(OrderYaml, reply, store);
            _ = await a.RunTurnAsync("q0", Ct);
            await a.FlushTranscriptAsync();
            await SaveAnotherSessionsTurnAsync(
                store, a.ConversationId, new ConversationSessionState { NextTurnIndex = 2, Stage = "closed", IsComplete = true });

            Exception? refused = await Record.ExceptionAsync(() => a.RunTurnAsync("q2", Ct));

            Assert.IsType<InvalidOperationException>(refused);
            IReadOnlyList<ConversationMessage> rows = await store.ReadForSessionAsync(a.ConversationId, Ct);
            Assert.Equal(4, rows.Count);
        }

        /// <summary>Saves turn 1 as another session of the conversation would: its words, with the state after them.</summary>
        private static async Task SaveAnotherSessionsTurnAsync(InMemoryConversationStore store, string conversationId, ConversationSessionState state)
        {
            _ = await store.AppendAsync(
                conversationId,
                [
                    new ConversationMessageDraft(1, new ChatMessage(ChatRole.User, "q1"), "b-user"),
                    new ConversationMessageDraft(1, new ChatMessage(ChatRole.Assistant, "b"), "b-reply"),
                ],
                state,
                Ct);
        }
    }
}

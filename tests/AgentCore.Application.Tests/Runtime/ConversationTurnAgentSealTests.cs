using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// When <see cref="ConversationTurnAgent"/> seals a turn and when it does not: a nested run never seals, a fault
    /// above every fallback is sealed as a fault, and an abandoned run still closes the stream beneath it.
    /// </summary>
    public sealed class ConversationTurnAgentSealTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // A nested run shares its parent's turn but never seals it.
        [Fact]
        public async Task ANestedRun_PassesStraightThrough_AndLeavesTheParentTurnUnsealed()
        {
            // Arrange
            Opened o = await OpenAsync(new ScriptedRunAgent(["inner words"]));
            TurnInvocation nested = o.Turn.Invocation with { Nested = true };

            // Act
            List<AgentResponseUpdate> updates = [];
            await foreach (AgentResponseUpdate update in o.Agent.RunStreamingAsync([nested.User!], o.Session, nested.RunOptions(), Ct))
            {
                updates.Add(update);
            }

            await o.History.DrainAsync(o.Session);

            // Assert
            Assert.DoesNotContain("complete", o.Log);
            Assert.Equal(0, o.Store.Appends);
            Assert.DoesNotContain(updates, update => update.Contents.OfType<TurnCommittedContent>().Any());
            Assert.True(o.Turn.Slot.TrySeal(out _));
        }

        // A fault above every fallback still ends in a turn sealed with that fault, and the
        // reader's stream ends without it.
        [Fact]
        public async Task AFaultAboveEveryFallback_IsSealedAsAFault_AndTheReaderSeesNoException()
        {
            // Arrange
            Opened o = await OpenAsync(new ScriptedRunAgent(["Hel"], new InvalidOperationException("boom")));

            // Act
            Exception? thrown = await Record.ExceptionAsync(async () =>
            {
                await foreach (AgentResponseUpdate _ in o.Agent.RunStreamingAsync(
                    [o.Turn.Invocation.User!], o.Session, o.Turn.Invocation.RunOptions(), o.Turn.Slot.Token))
                {
                }
            });
            await o.History.DrainAsync(o.Session);

            // Assert
            Assert.Null(thrown);
            Assert.Equal(1, o.Store.Appends);
            Assert.Equal(["hi", "Hel", "fallback"], Texts(o.Store.Rows.Select(row => row.Content)));
        }

        // A reader that stops reading disposes the layer's stream, and the layer disposes the run
        // beneath it rather than leaving it suspended.
        [Fact]
        public async Task AReaderThatStopsReading_ClosesTheRunBeneathTheLayer()
        {
            // Arrange
            ScriptedRunAgent inner = new(["Hel", "lo"]);
            Opened o = await OpenAsync(inner);

            // Act
            await foreach (AgentResponseUpdate update in o.Agent.RunStreamingAsync(
                [o.Turn.Invocation.User!], o.Session, o.Turn.Invocation.RunOptions(), o.Turn.Slot.Token))
            {
                if (update.Text.Length > 0)
                {
                    break;
                }
            }

            await o.History.DrainAsync(o.Session);

            // Assert
            Assert.True(inner.StreamClosed);
            Assert.Equal(1, o.Store.Appends);
        }

        private static async Task<Opened> OpenAsync(ScriptedRunAgent inner)
        {
            RecordingConversationStore store = new();
            _ = await store.CreateAsync(ConversationTurnAgentHarness.ConversationId, Ct);
            AgentCoreChatHistoryProvider history = new(store);
            ConversationTurnAgent agent = new(inner, history);
            AgentSession session = await agent.CreateSessionAsync(Ct);
            _ = history.BeginConversation(session, ConversationTurnAgentHarness.ConversationId, []);
            history.BeginTurn(session, 0);
            List<string> log = [];
            HarnessTurn turn = HarnessTurn.Open(history, session, "hi", turnIndex: 0, carriesHistory: true, log);
            return new Opened(agent, session, turn, store, history, log);
        }

        private sealed record Opened(
            ConversationTurnAgent Agent,
            AgentSession Session,
            HarnessTurn Turn,
            RecordingConversationStore Store,
            AgentCoreChatHistoryProvider History,
            List<string> Log);

        private static List<string> Texts(IEnumerable<ChatMessage> messages)
        {
            return [.. messages.Where(message => message.Text.Length > 0).Select(message => message.Text)];
        }
    }
}

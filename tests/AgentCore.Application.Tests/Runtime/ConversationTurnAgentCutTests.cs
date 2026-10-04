using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// The Cut rule over the real <see cref="ConversationTurnAgent"/>: a cut, a host cancel and an abandoned stream
    /// each end in the turn's one append.
    /// </summary>
    public sealed class ConversationTurnAgentCutTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // A cut mid-text keeps the user and the shown text, in one append.
        [Fact]
        public async Task CutMidText_KeepsTheUserAndTheShownText_InOneAppend()
        {
            // Arrange
            ConversationTurnAgentHarness h = await ConversationTurnAgentHarness.CreateAsync(TurnScriptChatClient.Text("Hel", "lo ", "there"));
            HarnessTurn turn = h.Begin("hi", turnIndex: 0);

            // Act
            _ = await h.DrainAsync(turn, update =>
            {
                if (update.Text.Length > 0)
                {
                    _ = turn.Slot.TryCut(new TurnCut("He", TimeSpan.FromMilliseconds(300)));
                }
            });

            // Assert
            Assert.Equal(1, h.Store.Appends);
            Assert.Equal(["hi", "He"], Texts(h.Store.Rows.Select(row => row.Content)));
            Assert.Empty(h.Store.Rewrites);
        }

        // A cut before any output keeps the user message only, and the turn before stays untouched.
        [Fact]
        public async Task CutBeforeOutput_KeepsTheUserOnly_AndLeavesThePreviousTurnUntouched()
        {
            // Arrange
            TurnScriptChatClient model = TurnScriptChatClient.Sequence(["first"], ["second"]);
            model.GateBeforeCall = 1;
            ConversationTurnAgentHarness h = await ConversationTurnAgentHarness.CreateAsync(model);
            _ = await h.RunAsync("one", turnIndex: 0);
            HarnessTurn second = h.Begin("two", turnIndex: 1);

            // Act
            Task<List<AgentResponseUpdate>> drain = h.DrainAsync(second);
            await model.Gated.Task.WaitAsync(Ct);
            _ = second.Slot.TryCut(new TurnCut(string.Empty, null));
            model.Release.TrySetResult();
            _ = await drain;

            // Assert
            Assert.Equal(2, h.Store.Appends);
            Assert.Empty(h.Store.Rewrites);
            Assert.Equal(["one", "first", "two"], Texts(h.History.Read(h.Session)));
        }

        // A host cancel keeps the user and everything yielded, in one append the hook never fed.
        [Fact]
        public async Task HostCancel_KeepsTheUserAndEverythingYielded_InOneAppend()
        {
            // Arrange
            ConversationTurnAgentHarness h = await ConversationTurnAgentHarness.CreateAsync(TurnScriptChatClient.Text("Hel", "lo ", "there"));
            using CancellationTokenSource host = new();
            HarnessTurn turn = h.Begin("hi", turnIndex: 0, host: host);

            // Act
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.DrainAsync(turn, update =>
            {
                if (update.Text.Length > 0)
                {
                    host.Cancel();
                }
            }));
            await h.History.DrainAsync(h.Session);

            // Assert
            Assert.Empty(turn.Completer.StagedAtComplete);
            Assert.Equal(1, h.Store.Appends);
            Assert.Equal(["hi", "Hel"], Texts(h.Store.Rows.Select(row => row.Content)));
        }

        // A reader that abandons the stream still gets the turn committed, from the layer's finally.
        [Fact]
        public async Task ReaderAbandonsTheStream_TheLayerCommitsTheUserAndTheYieldedText_InOneAppend()
        {
            // Arrange
            ConversationTurnAgentHarness h = await ConversationTurnAgentHarness.CreateAsync(TurnScriptChatClient.Text("Hel", "lo ", "there"));
            HarnessTurn turn = h.Begin("hi", turnIndex: 0);

            // Act
            await foreach (AgentResponseUpdate update in h.Agent.RunStreamingAsync(
                [turn.Invocation.User!], h.Session, turn.Invocation.RunOptions(), turn.Slot.Token))
            {
                if (update.Text.Length > 0)
                {
                    break;
                }
            }

            await h.History.DrainAsync(h.Session);

            // Assert
            Assert.DoesNotContain("staged", h.Log);
            Assert.Equal(1, h.Store.Appends);
            Assert.Equal(["hi", "Hel"], Texts(h.Store.Rows.Select(row => row.Content)));
        }

        private static List<string> Texts(IEnumerable<ChatMessage> messages)
        {
            return [.. messages.Where(message => message.Text.Length > 0).Select(message => message.Text)];
        }
    }
}

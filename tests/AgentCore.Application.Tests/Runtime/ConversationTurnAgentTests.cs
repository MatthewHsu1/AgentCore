using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Evaluation.Fakes;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// <see cref="ConversationTurnAgent"/> as the one trigger of a turn's durable write, tested
    /// over the real layer, provider, fallback and moderation layers.
    /// </summary>
    public sealed class ConversationTurnAgentTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // The hook stages, the stage machine runs, then one write holds the words and the fresh state.
        [Fact]
        public async Task CompletedTurn_HookStagesThenCompleteThenOneAppend_WithTheStateAfterTheAdvance()
        {
            // Arrange
            ConversationTurnAgentHarness h = await ConversationTurnAgentHarness.CreateAsync(
                TurnScriptChatClient.ToolThenText("lookup", "it is 42"));

            // Act
            HarnessTurn turn = await h.RunAsync("price?", turnIndex: 0);

            // Assert
            Assert.Equal(["staged", "complete", "append"], h.Log);
            Assert.Equal(1, h.Store.Appends);
            Assert.Equal(["user:text", "assistant:call", "tool:result", "assistant:text"], Kinds(h.Store.Rows.Select(row => row.Content)));
            Assert.Equal(Kinds(turn.Completer.StagedAtComplete), Kinds(h.Store.Rows.Skip(1).Select(row => row.Content)));
            Assert.Equal("stage-after:it is 42", Assert.Single(h.Store.States)!.Stage);
            Assert.Empty(h.History.Staged(h.Session));
        }

        // A buffered caller gets the reply, and the turn is the same single write.
        [Fact]
        public async Task BufferedCaller_GetsTheReply_AndTheTurnIsOneAppendFromTheHook()
        {
            // Arrange
            ConversationTurnAgentHarness h = await ConversationTurnAgentHarness.CreateAsync(TurnScriptChatClient.Text("hello"));
            HarnessTurn turn = h.Begin("hi", turnIndex: 0);

            // Act
            AgentResponse response = await h.Agent.RunAsync(
                [turn.Invocation.User!], h.Session, turn.Invocation.RunOptions(), turn.Slot.Token);
            await h.History.DrainAsync(h.Session);

            // Assert
            Assert.Equal("hello", response.Text);
            Assert.DoesNotContain(response.Messages.SelectMany(message => message.Contents), content => content is TurnCommittedContent);
            Assert.Equal(1, h.Store.Appends);
            Assert.NotEmpty(turn.Completer.StagedAtComplete);
            Assert.Equal(["hi", "hello"], Texts(h.Store.Rows.Select(row => row.Content)));
        }

        // A tool fault keeps the user, the finished pair and the fallback, in one write the hook never fed.
        [Fact]
        public async Task ToolFault_KeepsTheUserTheFinishedPairAndTheFallback_InOneAppend()
        {
            // Arrange
            ConversationTurnAgentHarness h = await ConversationTurnAgentHarness.CreateAsync(TurnScriptChatClient.ToolThenFault("lookup"));

            // Act
            HarnessTurn turn = await h.RunAsync("price?", turnIndex: 0);

            // Assert
            Assert.Empty(turn.Completer.StagedAtComplete);
            Assert.Equal(1, h.Store.Appends);
            Assert.Equal(["user:text", "assistant:call", "tool:result", "assistant:text"], Kinds(h.Store.Rows.Select(row => row.Content)));
            Assert.Equal("fallback", h.Store.Rows[^1].Content.Text);
        }

        // A moderation refusal keeps the user and the refusal in one write, and the model is never called.
        [Fact]
        public async Task ModerationRefusal_KeepsTheUserAndTheRefusal_InOneAppend()
        {
            // Arrange
            TurnScriptChatClient model = TurnScriptChatClient.Text("never");
            ConversationTurnAgentHarness h = await ConversationTurnAgentHarness.CreateAsync(model, ScriptedModerationEvaluator.Flagging("hate"));

            // Act
            _ = await h.RunAsync("...", turnIndex: 0);

            // Assert
            Assert.Equal(1, h.Store.Appends);
            Assert.Equal(["...", "refused"], Texts(h.Store.Rows.Select(row => row.Content)));
            Assert.Equal(0, model.Calls);
        }

        // The hook stages the empty reply and never sees the fallback; the seal writes the fallback.
        [Fact]
        public async Task EmptyReply_HookStagesNoFallback_TheSealWritesIt_InOneAppend()
        {
            // Arrange
            ConversationTurnAgentHarness h = await ConversationTurnAgentHarness.CreateAsync(TurnScriptChatClient.Text("   "));

            // Act
            HarnessTurn turn = await h.RunAsync("hi", turnIndex: 0);

            // Assert
            Assert.NotEmpty(turn.Completer.StagedAtComplete);
            Assert.DoesNotContain("fallback", turn.Completer.StagedAtComplete.Select(message => message.Text));
            Assert.Equal(1, h.Store.Appends);
            Assert.Equal(["hi", "fallback"], Texts(h.Store.Rows.Select(row => row.Content)));
        }

        // A graph's output reaches the conversation through the layer; the node sessions write nothing.
        [Fact]
        public async Task GraphRow_TheOutputReachesTheConversationThroughTheLayer_TheNodesWriteNothing()
        {
            // Arrange
            ConversationTurnAgentHarness h = await ConversationTurnAgentHarness.CreateAsync(TurnScriptChatClient.Text("unused"));
            ChatClientAgent first = ConversationTurnAgentHarness.ToolAgent(TurnScriptChatClient.Text("one"), h.History, "first");
            ChatClientAgent second = ConversationTurnAgentHarness.ToolAgent(TurnScriptChatClient.Text("two"), h.History, "second");
            AIAgent graph = AgentWorkflowBuilder.BuildSequential("g", [first, second]).AsAIAgent(name: "g");
            ConversationTurnAgent layered = new(graph, h.History);
            AgentSession run = await graph.CreateSessionAsync(Ct);
            h.History.BeginTurn(h.Session, 0);
            HarnessTurn turn = HarnessTurn.Open(h.History, h.Session, "hi", turnIndex: 0, carriesHistory: false, h.Log);

            // Act
            await foreach (AgentResponseUpdate _ in layered.RunStreamingAsync(
                [turn.Invocation.User!], run, turn.Invocation.RunOptions(), turn.Slot.Token))
            {
            }

            await h.History.DrainAsync(h.Session);

            // Assert
            Assert.Equal(1, h.Store.Appends);
            Assert.All(h.Store.Rows, row => Assert.Equal(ConversationTurnAgentHarness.ConversationId, row.ConversationId));
            Assert.Equal(["hi", "two"], Texts(h.Store.Rows.Select(row => row.Content)));
        }

        // The committed ids reach a streaming caller on the turn's last update.
        [Fact]
        public async Task StreamingCaller_GetsTheCommittedIds_OnTheLastUpdate()
        {
            // Arrange
            ConversationTurnAgentHarness h = await ConversationTurnAgentHarness.CreateAsync(TurnScriptChatClient.Text("hello"));

            // Act
            HarnessTurn turn = await h.RunAsync("hi", turnIndex: 0);

            // Assert
            TurnCommittedContent committed = Assert.Single(turn.Updates[^1].Contents.OfType<TurnCommittedContent>());
            Assert.Equal(h.Store.Rows[0].MessageId, committed.UserMessageId);
            Assert.Equal(h.Store.Rows[^1].MessageId, committed.ReplyMessageId);
        }

        // An edit is a provider command the layer triggers: the truncate, then one append, and the model
        // no longer reads the withdrawn words.
        [Fact]
        public async Task Edit_TheLayerTruncatesThenAppendsOnce_AndTheModelNoLongerReadsTheWithdrawnWords()
        {
            // Arrange
            TurnScriptChatClient model = TurnScriptChatClient.Sequence(["a1"], ["a2"], ["a3"]);
            ConversationTurnAgentHarness h = await ConversationTurnAgentHarness.CreateAsync(model);
            HarnessTurn first = await h.RunAsync("q1", turnIndex: 0);
            _ = await h.RunAsync("q2", turnIndex: 1);
            ConversationTurnOrigin origin = new(MessageId: null, ParentMessageId: first.Completer.Ids!.Value.ReplyMessageId) { NamesParent = true };

            // Act
            HarnessTurn edited = await h.RunAsync("q2 edited", turnIndex: 2, origin);

            // Assert
            Assert.Equal(new WithdrawnTurns(1, 1), edited.Completer.Withdrawn);
            Assert.Equal(3, h.Store.Appends);
            Assert.Equal(["q1", "a1", "q2 edited", "a3"], Texts(h.History.Read(h.Session)));
            Assert.DoesNotContain(model.Requests[2], message => message.Text is "q2" or "a2");
        }

        private static List<string> Texts(IEnumerable<ChatMessage> messages)
        {
            return [.. messages.Where(message => message.Text.Length > 0).Select(message => message.Text)];
        }

        private static List<string> Kinds(IEnumerable<ChatMessage> messages)
        {
            return [.. messages.Select(message => $"{message.Role}:{Kind(message)}")];
        }

        private static string Kind(ChatMessage message)
        {
            return message.Contents.Any(content => content is FunctionCallContent) ? "call"
                : message.Contents.Any(content => content is FunctionResultContent) ? "result"
                : "text";
        }
    }
}

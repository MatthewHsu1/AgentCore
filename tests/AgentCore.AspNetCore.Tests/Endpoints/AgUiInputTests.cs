using System.Text.Json;
using AGUI.Abstractions;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Endpoints;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// The AG-UI route's input rules: the newest user message with text is the turn's words,
    /// extra forwarded properties are dropped, URL parts never reach the model, the author
    /// name is cleared, and an oversized message is refused before a turn starts.
    /// </summary>
    public sealed class AgUiInputTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static RunAgentInput Input(params AGUIMessage[] messages)
        {
            RunAgentInput input = new() { ThreadId = "t", RunId = "r" };
            foreach (AGUIMessage message in messages)
            {
                input.Messages.Add(message);
            }

            return input;
        }

        private static RunAgentInput WithForwarded(string json)
        {
            using JsonDocument forwarded = JsonDocument.Parse(json);
            RunAgentInput input = Input(new AGUIUserMessage { Id = "u1", Content = "hi" });
            input.ForwardedProperties = forwarded.RootElement.Clone();
            return input;
        }

        private static Conversations Door(InMemoryConversationStore store)
        {
            return new Conversations(store, blobs: null);
        }

        private static async Task<InMemoryConversationStore> SeedTurnsAsync(string conversationId)
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync(conversationId, Ct);
            _ = await store.AppendAsync(
                conversationId,
                [
                    new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "first"), "u-a"),
                    new ConversationMessageDraft(0, new ChatMessage(ChatRole.Assistant, "answer one"), "a-a"),
                    new ConversationMessageDraft(1, new ChatMessage(ChatRole.User, "second"), "u-b"),
                    new ConversationMessageDraft(1, new ChatMessage(ChatRole.Assistant, "answer two"), "a-b"),
                ],
                state: null,
                cancellationToken: Ct);
            return store;
        }

        [Fact]
        public void TurnIds_NameBlocks_TurnKnResolvesTurnK()
        {
            Assert.Equal("turn-3", AgUiTurnIds.TurnMessageId(3));
            Assert.Equal("turn-3.1", AgUiTurnIds.BlockMessageId(3, 1));
            Assert.Equal(3, AgUiTurnIds.TurnIndexOf("turn-3.1"));
            Assert.Equal(3, AgUiTurnIds.TurnIndexOf("turn-3"));
            Assert.Null(AgUiTurnIds.TurnIndexOf("u-1"));
            Assert.Null(AgUiTurnIds.TurnIndexOf("turn-x"));
            Assert.Null(AgUiTurnIds.TurnIndexOf(null));
        }

        [Fact]
        public void NewestUserText_Picked_OthersIgnored()
        {
            List<ChatMessage> messages =
            [
                new(ChatRole.System, "be brief"),
                new(ChatRole.User, "old"),
                new(ChatRole.Assistant, "old answer"),
                new(ChatRole.User, "new"),
                new(ChatRole.User, string.Empty),
            ];

            Assert.Equal("new", AgUiInput.LastUserInput(messages)?.Text);
            Assert.Null(AgUiInput.LastUserInput([new(ChatRole.System, "be brief"), new(ChatRole.Assistant, "hi")]));
            Assert.Null(AgUiInput.LastUserInput([new(ChatRole.User, string.Empty)]));
        }

        [Fact]
        public void ForgedToolsSystemContextStateIgnored_OnlyAnchorRead()
        {
            RunAgentInput input = WithForwarded(
                """{"agentcore":{"message_id":"m-1","parent_id":"p-1"},"runConfig":{"model":"evil"},"temperature":0.9}""");

            AgUiTurnIds.Anchor anchor = AgUiTurnIds.Read(input);

            Assert.Equal(("m-1", "p-1", true), (anchor.MessageId, anchor.ParentId, anchor.NamesParent));
        }

        [Fact]
        public void UrlImagePart_Dropped_ModelGetsNoUrl()
        {
            ChatMessage message = new(ChatRole.User, [new TextContent("see this"), new UriContent("https://x/y.png", "image/png")]);

            ChatMessage sanitized = AgUiInput.Sanitize(message);

            Assert.DoesNotContain(sanitized.Contents, content => content is UriContent);
            Assert.Contains(sanitized.Contents, content => content is TextContent text && text.Text == "see this");
        }

        [Fact]
        public void UserName_Cleared()
        {
            ChatMessage message = new(ChatRole.User, "hi") { AuthorName = "Mallory" };

            ChatMessage sanitized = AgUiInput.Sanitize(message);

            Assert.Null(sanitized.AuthorName);
        }

        [Fact]
        public void OversizedMessage_RefusedBelow30MB()
        {
            ChatMessage message = new(ChatRole.User, new string('x', (5 * 1024 * 1024) + 1));

            (int Status, string Code, string Message)? refusal = AgUiInput.CheckSize(message);

            Assert.NotNull(refusal);
            Assert.Equal((400, "message_too_large"), (refusal.Value.Status, refusal.Value.Code));
        }

        [Fact]
        public async Task ParentTurnK_ResolvesToLastStoredRowOfTurnK()
        {
            InMemoryConversationStore store = await SeedTurnsAsync("c1");

            ConversationTurnOrigin origin = await AgUiTurnIds.OriginAsync(
                new AgUiTurnIds.Anchor("u-new", AgUiTurnIds.TurnMessageId(0), true), "c1", Door(store), Ct);

            Assert.Equal("a-a", origin.ParentMessageId);
        }

        [Fact]
        public async Task ParentTurnKn_ResolvesToSameRow()
        {
            InMemoryConversationStore store = await SeedTurnsAsync("c1");

            ConversationTurnOrigin origin = await AgUiTurnIds.OriginAsync(
                new AgUiTurnIds.Anchor("u-new", AgUiTurnIds.BlockMessageId(0, 1), true), "c1", Door(store), Ct);

            Assert.Equal("a-a", origin.ParentMessageId);
        }

        [Fact]
        public async Task ParentNull_WithdrawsWholeConversation()
        {
            InMemoryConversationStore store = await SeedTurnsAsync("c1");

            ConversationTurnOrigin origin = await AgUiTurnIds.OriginAsync(
                new AgUiTurnIds.Anchor("u-new", null, true), "c1", Door(store), Ct);

            Assert.Equal((null, true), (origin.ParentMessageId, origin.NamesParent));
        }

        [Fact]
        public async Task UnknownParent_RunsAsPlainNewTurn()
        {
            InMemoryConversationStore store = await SeedTurnsAsync("c1");

            ConversationTurnOrigin origin = await AgUiTurnIds.OriginAsync(
                new AgUiTurnIds.Anchor("u-new", "no-such-row", true), "c1", Door(store), Ct);

            Assert.Equal("no-such-row", origin.ParentMessageId);
        }
    }
}

using System.Runtime.CompilerServices;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Llm;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Conversation
{
    /// <summary>Making a conversation's title from its words.</summary>
    public sealed class ChatConversationTitlerTests
    {
        private static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public async Task GenerateAsync_AConversationWithWords_StreamsThePiecesAndStoresTheWhole()
        {
            // Arrange
            (ChatConversationTitler? titler, InMemoryConversationStore? conversations) = await BuildAsync("A squeaky ", "belt");

            // Act
            List<string> pieces = await CollectAsync(titler.GenerateAsync("c1", Token));

            // Assert
            Assert.Equal(["A squeaky ", "belt"], pieces);
            Assert.Equal("A squeaky belt", (await conversations.GetAsync("c1", Token))!.Title);
        }

        [Fact]
        public async Task GenerateAsync_AConversationWithNoWords_YieldsNothingAndLeavesTheTitleAlone()
        {
            // Arrange
            InMemoryConversationStore conversations = new();
            _ = await conversations.CreateAsync("c1", Token);
            await conversations.RenameAsync("c1", "kept", Token);
            ChatConversationTitler titler = new(conversations, new StubChatClient("ignored"));

            // Act
            List<string> pieces = await CollectAsync(titler.GenerateAsync("c1", Token));

            // Assert
            Assert.Empty(pieces);
            Assert.Equal("kept", (await conversations.GetAsync("c1", Token))!.Title);
        }

        [Fact]
        public async Task GenerateAsync_StoppedPartWay_LeavesTheTitleAlone()
        {
            // Arrange
            (ChatConversationTitler? titler, InMemoryConversationStore? conversations) = await BuildAsync("A squeaky ", "belt");
            await conversations.RenameAsync("c1", "kept", Token);

            // Act
            await foreach (string _ in titler.GenerateAsync("c1", Token))
            {
                break;
            }

            // Assert
            Assert.Equal("kept", (await conversations.GetAsync("c1", Token))!.Title);
        }

        [Fact]
        public async Task GenerateAsync_OneTurnWritingManyMessages_SendsOnlyTheNewestSix()
        {
            // Arrange: the store reads whole turns, so one wide turn hands back more than six rows.
            // The cut is by message, not by turn.
            InMemoryConversationStore conversations = new();
            _ = await conversations.CreateAsync("c1", Token);
            _ = await conversations.AppendAsync(
                "c1",
                [
                    new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "early"), "m0"),
                    .. Enumerable.Range(1, 7).Select(n => new ConversationMessageDraft(1, new ChatMessage(ChatRole.User, $"m{n}"), $"m{n}")),
                ],
                cancellationToken: Token);
            StubChatClient client = new("title");
            ChatConversationTitler titler = new(conversations, client);

            // Act
            _ = await CollectAsync(titler.GenerateAsync("c1", Token));

            // Assert
            string transcript = Assert.Single(client.Seen, message => message.Role == ChatRole.User).Text;
            Assert.Equal("user: m2\nuser: m3\nuser: m4\nuser: m5\nuser: m6\nuser: m7\n", transcript.ReplaceLineEndings("\n"));
        }

        [Fact]
        public async Task GenerateFromAsync_MessagesFromTheCaller_StreamsThePiecesAndStoresTheWhole()
        {
            // Arrange
            // The conversation holds no messages at all, so a title here can only have come from the caller's.
            InMemoryConversationStore conversations = new();
            _ = await conversations.CreateAsync("c1", Token);
            ChatConversationTitler titler = new(conversations, new StubChatClient("A squeaky ", "belt"));

            // Act
            List<string> pieces = await CollectAsync(titler.GenerateFromAsync("c1", Said("my belt squeaks"), Token));

            // Assert
            Assert.Equal(["A squeaky ", "belt"], pieces);
            Assert.Equal("A squeaky belt", (await conversations.GetAsync("c1", Token))!.Title);
        }

        [Fact]
        public async Task GenerateFromAsync_NoMessagesFromTheCaller_YieldsNothingAndLeavesTheTitleAlone()
        {
            // Arrange
            InMemoryConversationStore conversations = new();
            _ = await conversations.CreateAsync("c1", Token);
            await conversations.RenameAsync("c1", "kept", Token);
            ChatConversationTitler titler = new(conversations, new StubChatClient("ignored"));

            // Act
            List<string> pieces = await CollectAsync(titler.GenerateFromAsync("c1", [], Token));

            // Assert
            Assert.Empty(pieces);
            Assert.Equal("kept", (await conversations.GetAsync("c1", Token))!.Title);
        }

        [Fact]
        public async Task GenerateFromAsync_StoppedPartWay_LeavesTheTitleAlone()
        {
            // Arrange
            InMemoryConversationStore conversations = new();
            _ = await conversations.CreateAsync("c1", Token);
            await conversations.RenameAsync("c1", "kept", Token);
            ChatConversationTitler titler = new(conversations, new StubChatClient("A squeaky ", "belt"));

            // Act
            await foreach (string _ in titler.GenerateFromAsync("c1", Said("my belt squeaks"), Token))
            {
                break;
            }

            // Assert
            Assert.Equal("kept", (await conversations.GetAsync("c1", Token))!.Title);
        }

        [Fact]
        public async Task GenerateFromAsync_AnUnknownConversation_YieldsNothingAndNeverAsksTheModel()
        {
            // Arrange
            InMemoryConversationStore conversations = new();
            StubChatClient client = new("A squeaky belt");
            ChatConversationTitler titler = new(conversations, client);

            // Act
            List<string> pieces = await CollectAsync(titler.GenerateFromAsync("missing", Said("my belt squeaks"), Token));

            // Assert
            Assert.Empty(pieces);
            Assert.Empty(client.Seen);
        }

        [Fact]
        public async Task GenerateFromAsync_MoreMessagesThanTheCap_SendsOnlyTheFirstSix()
        {
            // Arrange
            InMemoryConversationStore conversations = new();
            _ = await conversations.CreateAsync("c1", Token);
            StubChatClient client = new("A squeaky belt");
            ChatConversationTitler titler = new(conversations, client);
            List<ChatMessage> many = [.. Enumerable.Range(0, 9).Select(n => new ChatMessage(ChatRole.User, $"m{n}"))];

            // Act
            _ = await CollectAsync(titler.GenerateFromAsync("c1", many, Token));

            // Assert
            // The instruction is the system message; the caller's words are folded into the one user message.
            string transcript = Assert.Single(client.Seen, message => message.Role == ChatRole.User).Text;
            Assert.Equal("user: m0\nuser: m1\nuser: m2\nuser: m3\nuser: m4\nuser: m5\n", transcript.ReplaceLineEndings("\n"));
        }

        [Fact]
        public async Task GenerateFromAsync_Always_SendsTheInstructionAsTheSystemMessageAndTheWordsAsTheUser()
        {
            // Arrange
            InMemoryConversationStore conversations = new();
            _ = await conversations.CreateAsync("c1", Token);
            StubChatClient client = new("A squeaky belt");
            ChatConversationTitler titler = new(conversations, client);

            // Act
            _ = await CollectAsync(titler.GenerateFromAsync("c1", Said("my belt squeaks"), Token));

            // Assert
            Assert.Collection(
                client.Seen,
                message => Assert.Equal(ChatRole.System, message.Role),
                message =>
                {
                    Assert.Equal(ChatRole.User, message.Role);
                    Assert.Equal("user: my belt squeaks", message.Text.Trim());
                });
        }

        [Fact]
        public async Task EveryRequest_CarriesTheConversationIdAnAdapterRoutesOn()
        {
            // Arrange: the title runs outside a turn, so nothing else stamps the id on the request.
            InMemoryConversationStore conversations = new();
            _ = await conversations.CreateAsync("c1", Token);
            StubChatClient client = new("title");
            ChatConversationTitler titler = new(conversations, client);

            // Act
            _ = await CollectAsync(titler.GenerateFromAsync("c1", Said("my belt squeaks"), Token));

            // Assert
            Assert.True(client.SeenOptions!.AdditionalProperties!.TryGetValue(
                ChatRequestProperties.ConversationId, out string? conversationId));
            Assert.Equal("c1", conversationId);
        }

        private static List<ChatMessage> Said(string words)
        {
            return [new ChatMessage(ChatRole.User, words)];
        }

        private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> stream)
        {
            List<string> pieces = [];

            await foreach (string piece in stream)
            {
                pieces.Add(piece);
            }

            return pieces;
        }

        private static async Task<(ChatConversationTitler Titler, InMemoryConversationStore Conversations)> BuildAsync(params string[] pieces)
        {
            InMemoryConversationStore conversations = new();
            _ = await conversations.CreateAsync("c1", Token);
            _ = await conversations.AppendAsync(
                "c1",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "my belt squeaks"), "m0")],
                cancellationToken: Token);

            return (new ChatConversationTitler(conversations, new StubChatClient(pieces)), conversations);
        }

        private sealed class StubChatClient(params string[] pieces) : IChatClient
        {
            public List<ChatMessage> Seen { get; } = [];

            public ChatOptions? SeenOptions { get; private set; }

            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("The titler streams.");
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Seen.AddRange(messages);
                SeenOptions = options;

                foreach (string piece in pieces)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new ChatResponseUpdate(ChatRole.Assistant, piece);
                    await Task.Yield();
                }
            }

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }
    }
}

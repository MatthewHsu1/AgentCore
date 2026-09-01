using System.Runtime.CompilerServices;
using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Calls;

/// <summary>Making a call's title from its words.</summary>
public sealed class ChatCallTitlerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GenerateAsync_ACallWithWords_StreamsThePiecesAndStoresTheWhole()
    {
        // Arrange
        var (titler, calls) = await BuildAsync("A squeaky ", "belt");

        // Act
        var pieces = await CollectAsync(titler.GenerateAsync("c1", Token));

        // Assert
        Assert.Equal(["A squeaky ", "belt"], pieces);
        Assert.Equal("A squeaky belt", (await calls.GetAsync("c1", Token))!.Title);
    }

    [Fact]
    public async Task GenerateAsync_ACallWithNoWords_YieldsNothingAndLeavesTheTitleAlone()
    {
        // Arrange
        InMemoryCallStore calls = new();
        await calls.CreateAsync("c1", Token);
        await calls.RenameAsync("c1", "kept", Token);
        ChatCallTitler titler = new(calls, new StubChatClient("ignored"));

        // Act
        var pieces = await CollectAsync(titler.GenerateAsync("c1", Token));

        // Assert
        Assert.Empty(pieces);
        Assert.Equal("kept", (await calls.GetAsync("c1", Token))!.Title);
    }

    [Fact]
    public async Task GenerateAsync_StoppedPartWay_LeavesTheTitleAlone()
    {
        // Arrange
        var (titler, calls) = await BuildAsync("A squeaky ", "belt");
        await calls.RenameAsync("c1", "kept", Token);

        // Act
        await foreach (var _ in titler.GenerateAsync("c1", Token))
        {
            break;
        }

        // Assert
        Assert.Equal("kept", (await calls.GetAsync("c1", Token))!.Title);
    }

    [Fact]
    public async Task GenerateFromAsync_MessagesFromTheCaller_StreamsThePiecesAndStoresTheWhole()
    {
        // Arrange
        // The call holds no messages at all, so a title here can only have come from the caller's.
        InMemoryCallStore calls = new();
        await calls.CreateAsync("c1", Token);
        ChatCallTitler titler = new(calls, new StubChatClient("A squeaky ", "belt"));

        // Act
        var pieces = await CollectAsync(titler.GenerateFromAsync("c1", Said("my belt squeaks"), Token));

        // Assert
        Assert.Equal(["A squeaky ", "belt"], pieces);
        Assert.Equal("A squeaky belt", (await calls.GetAsync("c1", Token))!.Title);
    }

    [Fact]
    public async Task GenerateFromAsync_NoMessagesFromTheCaller_YieldsNothingAndLeavesTheTitleAlone()
    {
        // Arrange
        InMemoryCallStore calls = new();
        await calls.CreateAsync("c1", Token);
        await calls.RenameAsync("c1", "kept", Token);
        ChatCallTitler titler = new(calls, new StubChatClient("ignored"));

        // Act
        var pieces = await CollectAsync(titler.GenerateFromAsync("c1", [], Token));

        // Assert
        Assert.Empty(pieces);
        Assert.Equal("kept", (await calls.GetAsync("c1", Token))!.Title);
    }

    [Fact]
    public async Task GenerateFromAsync_StoppedPartWay_LeavesTheTitleAlone()
    {
        // Arrange
        InMemoryCallStore calls = new();
        await calls.CreateAsync("c1", Token);
        await calls.RenameAsync("c1", "kept", Token);
        ChatCallTitler titler = new(calls, new StubChatClient("A squeaky ", "belt"));

        // Act
        await foreach (var _ in titler.GenerateFromAsync("c1", Said("my belt squeaks"), Token))
        {
            break;
        }

        // Assert
        Assert.Equal("kept", (await calls.GetAsync("c1", Token))!.Title);
    }

    [Fact]
    public async Task GenerateFromAsync_AnUnknownCall_YieldsNothingAndNeverAsksTheModel()
    {
        // Arrange
        InMemoryCallStore calls = new();
        StubChatClient client = new("A squeaky belt");
        ChatCallTitler titler = new(calls, client);

        // Act
        var pieces = await CollectAsync(titler.GenerateFromAsync("missing", Said("my belt squeaks"), Token));

        // Assert
        Assert.Empty(pieces);
        Assert.Empty(client.Seen);
    }

    [Fact]
    public async Task GenerateFromAsync_MoreMessagesThanTheCap_SendsOnlyTheFirstSix()
    {
        // Arrange
        InMemoryCallStore calls = new();
        await calls.CreateAsync("c1", Token);
        StubChatClient client = new("A squeaky belt");
        ChatCallTitler titler = new(calls, client);
        List<ChatMessage> many = [.. Enumerable.Range(0, 9).Select(n => new ChatMessage(ChatRole.User, $"m{n}"))];

        // Act
        await CollectAsync(titler.GenerateFromAsync("c1", many, Token));

        // Assert
        // The titler appends the instruction as the last message; everything before it is the caller's.
        Assert.Equal(
            ["m0", "m1", "m2", "m3", "m4", "m5"],
            client.Seen.SkipLast(1).Select(message => message.Text));
    }

    private static List<ChatMessage> Said(string words) => [new ChatMessage(ChatRole.User, words)];

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> stream)
    {
        List<string> pieces = [];

        await foreach (var piece in stream)
        {
            pieces.Add(piece);
        }

        return pieces;
    }

    private static async Task<(ChatCallTitler Titler, InMemoryCallStore Calls)> BuildAsync(params string[] pieces)
    {
        InMemoryCallStore calls = new();
        await calls.CreateAsync("c1", Token);
        await calls.AppendAsync(
            [new CallMessage("c1", 0, 0, new ChatMessage(ChatRole.User, "my belt squeaks"), "m0")],
            cancellationToken: Token);

        return (new ChatCallTitler(calls, new StubChatClient(pieces)), calls);
    }

    private sealed class StubChatClient(params string[] pieces) : IChatClient
    {
        public List<ChatMessage> Seen { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The titler streams.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Seen.AddRange(messages);

            foreach (var piece in pieces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, piece);
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

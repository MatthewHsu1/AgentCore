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
        List<string> pieces = [];
        await foreach (var piece in titler.GenerateAsync("c1", Token))
        {
            pieces.Add(piece);
        }

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
        List<string> pieces = [];
        await foreach (var piece in titler.GenerateAsync("c1", Token))
        {
            pieces.Add(piece);
        }

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

    private static async Task<(ChatCallTitler Titler, InMemoryCallStore Calls)> BuildAsync(params string[] pieces)
    {
        InMemoryCallStore calls = new();
        await calls.CreateAsync("c1", Token);
        await calls.AppendAsync(
            [new CallMessage("c1", 0, 0, new ChatMessage(ChatRole.User, "my belt squeaks"))],
            Token);

        return (new ChatCallTitler(calls, new StubChatClient(pieces)), calls);
    }

    private sealed class StubChatClient(params string[] pieces) : IChatClient
    {
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

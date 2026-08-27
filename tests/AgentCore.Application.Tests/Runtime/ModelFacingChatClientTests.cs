using System.Text.Json;
using AgentCore.Application.Runtime;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// <see cref="ModelFacingChatClient"/> is the only seam that strips a <see cref="RenderContent"/>
/// before the model reads it. Every assertion here is against what the fake inner
/// <see cref="IChatClient"/> received, never against the returned response, because the strip must
/// happen to the outgoing request and nothing else.
/// </summary>
public sealed class ModelFacingChatClientTests
{
    private static readonly JsonElement Payload = JsonDocument.Parse("""{"x":1}""").RootElement.Clone();

    [Fact]
    public async Task GetResponseAsync_StripsRenderContentBeforeTheInnerClientSeesIt()
    {
        var inner = new SequencedChatClient("ok");
        var client = new ModelFacingChatClient(inner);
        var drew = new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("here you go"),
            new RenderContent { Name = "order-card", RenderId = "order-41", Data = Payload },
        ]);

        await client.GetResponseAsync([drew], ct: TestContext.Current.CancellationToken);

        var forwarded = Assert.Single(inner.Requests);
        var message = Assert.Single(forwarded);
        Assert.DoesNotContain(message.Contents, c => c is RenderContent);
        Assert.Single(message.Contents.OfType<TextContent>());
    }

    [Fact]
    public async Task GetStreamingResponseAsync_StripsRenderContentBeforeTheInnerClientSeesIt()
    {
        var inner = new SequencedChatClient("ok");
        var client = new ModelFacingChatClient(inner);
        var drew = new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("here you go"),
            new RenderContent { Name = "order-card", RenderId = "order-41", Data = Payload },
        ]);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [drew], ct: TestContext.Current.CancellationToken))
        {
        }

        var forwarded = Assert.Single(inner.Requests);
        var message = Assert.Single(forwarded);
        Assert.DoesNotContain(message.Contents, c => c is RenderContent);
    }

    [Fact]
    public async Task GetResponseAsync_PassesAMessageWithNoRenderContentThroughByReference()
    {
        var inner = new SequencedChatClient("ok");
        var client = new ModelFacingChatClient(inner);
        var plain = new ChatMessage(ChatRole.User, "hello");

        await client.GetResponseAsync([plain], ct: TestContext.Current.CancellationToken);

        var forwarded = Assert.Single(inner.Requests);
        Assert.Same(plain, Assert.Single(forwarded));
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PassesAMessageWithNoRenderContentThroughByReference()
    {
        var inner = new SequencedChatClient("ok");
        var client = new ModelFacingChatClient(inner);
        var plain = new ChatMessage(ChatRole.User, "hello");

        await foreach (var _ in client.GetStreamingResponseAsync(
            [plain], ct: TestContext.Current.CancellationToken))
        {
        }

        var forwarded = Assert.Single(inner.Requests);
        Assert.Same(plain, Assert.Single(forwarded));
    }

    [Fact]
    public async Task GetResponseAsync_RebuiltMessageCarriesAdditionalPropertiesAndIdentity()
    {
        var inner = new SequencedChatClient("ok");
        var client = new ModelFacingChatClient(inner);
        var drew = new ChatMessage(ChatRole.Assistant,
        [
            new RenderContent { Name = "order-card", RenderId = "order-41", Data = Payload },
        ])
        {
            MessageId = "msg-1",
            AuthorName = "assistant-1",
            CreatedAt = DateTimeOffset.UnixEpoch,
            AdditionalProperties = new AdditionalPropertiesDictionary { ["k"] = "v" },
        };

        await client.GetResponseAsync([drew], ct: TestContext.Current.CancellationToken);

        var forwarded = Assert.Single(Assert.Single(inner.Requests));
        Assert.Equal("msg-1", forwarded.MessageId);
        Assert.Equal("assistant-1", forwarded.AuthorName);
        Assert.Equal(DateTimeOffset.UnixEpoch, forwarded.CreatedAt);
        Assert.Equal("v", forwarded.AdditionalProperties?["k"]);
    }
}

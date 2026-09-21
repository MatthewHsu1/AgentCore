using System.Text.Json;
using AgentCore.Application.Runtime;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime
{
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
            SequencedChatClient inner = new("ok");
            ModelFacingChatClient client = new(inner);
            ChatMessage drew = new(ChatRole.Assistant,
            [
                new TextContent("here you go"),
                new RenderContent { Name = "order-card", RenderId = "order-41", Data = Payload },
            ]);

            _ = await client.GetResponseAsync([drew], cancellationToken: TestContext.Current.CancellationToken);

            List<ChatMessage> forwarded = Assert.Single(inner.Requests);
            ChatMessage message = Assert.Single(forwarded);
            Assert.DoesNotContain(message.Contents, c => c is RenderContent);
            _ = Assert.Single(message.Contents.OfType<TextContent>());
        }

        [Fact]
        public async Task GetStreamingResponseAsync_StripsRenderContentBeforeTheInnerClientSeesIt()
        {
            SequencedChatClient inner = new("ok");
            ModelFacingChatClient client = new(inner);
            ChatMessage drew = new(ChatRole.Assistant,
            [
                new TextContent("here you go"),
                new RenderContent { Name = "order-card", RenderId = "order-41", Data = Payload },
            ]);

            await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
                [drew], cancellationToken: TestContext.Current.CancellationToken))
            {
            }

            List<ChatMessage> forwarded = Assert.Single(inner.Requests);
            ChatMessage message = Assert.Single(forwarded);
            Assert.DoesNotContain(message.Contents, c => c is RenderContent);
        }

        [Fact]
        public async Task GetResponseAsync_PassesAMessageWithNoRenderContentThroughByReference()
        {
            SequencedChatClient inner = new("ok");
            ModelFacingChatClient client = new(inner);
            ChatMessage plain = new(ChatRole.User, "hello");

            _ = await client.GetResponseAsync([plain], cancellationToken: TestContext.Current.CancellationToken);

            List<ChatMessage> forwarded = Assert.Single(inner.Requests);
            Assert.Same(plain, Assert.Single(forwarded));
        }

        [Fact]
        public async Task GetStreamingResponseAsync_PassesAMessageWithNoRenderContentThroughByReference()
        {
            SequencedChatClient inner = new("ok");
            ModelFacingChatClient client = new(inner);
            ChatMessage plain = new(ChatRole.User, "hello");

            await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
                [plain], cancellationToken: TestContext.Current.CancellationToken))
            {
            }

            List<ChatMessage> forwarded = Assert.Single(inner.Requests);
            Assert.Same(plain, Assert.Single(forwarded));
        }

        [Fact]
        public async Task GetResponseAsync_RebuiltMessageCarriesAdditionalPropertiesAndIdentity()
        {
            SequencedChatClient inner = new("ok");
            ModelFacingChatClient client = new(inner);
            ChatMessage drew = new(ChatRole.Assistant,
            [
                new RenderContent { Name = "order-card", RenderId = "order-41", Data = Payload },
            ])
            {
                MessageId = "msg-1",
                AuthorName = "assistant-1",
                CreatedAt = DateTimeOffset.UnixEpoch,
                AdditionalProperties = new AdditionalPropertiesDictionary { ["k"] = "v" },
            };

            _ = await client.GetResponseAsync([drew], cancellationToken: TestContext.Current.CancellationToken);

            ChatMessage forwarded = Assert.Single(Assert.Single(inner.Requests));
            Assert.Equal("msg-1", forwarded.MessageId);
            Assert.Equal("assistant-1", forwarded.AuthorName);
            Assert.Equal(DateTimeOffset.UnixEpoch, forwarded.CreatedAt);
            Assert.Equal("v", forwarded.AdditionalProperties?["k"]);
        }
    }
}

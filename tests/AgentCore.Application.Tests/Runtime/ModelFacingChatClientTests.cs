using AgentCore.Application.Transcript;
using AgentCore.Domain.Sources;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.ToolCalls;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// <see cref="ModelFacingChatClient"/> is the only seam that strips a <see cref="SourceContent"/>
    /// before the model reads it. Every assertion here is against what the fake inner
    /// <see cref="IChatClient"/> received, never against the returned response, because the strip must
    /// happen to the outgoing request and nothing else.
    /// </summary>
    public sealed class ModelFacingChatClientTests
    {
        private static readonly SourceContent Citation = new()
        {
            Source = new SourceReference { SourceId = "order-41", Kind = SourceKind.Document, Title = "Order #41", Origin = "knowledge" },
            CallId = "call-1",
        };

        [Fact]
        public async Task GetResponseAsync_StripsSourceContentBeforeTheInnerClientSeesIt()
        {
            SequencedChatClient inner = new("ok");
            ModelFacingChatClient client = new(inner);
            ChatMessage cited = new(ChatRole.Assistant,
            [
                new TextContent("here you go"),
                Citation,
            ]);

            _ = await client.GetResponseAsync([cited], cancellationToken: TestContext.Current.CancellationToken);

            List<ChatMessage> forwarded = Assert.Single(inner.Requests);
            ChatMessage message = Assert.Single(forwarded);
            Assert.DoesNotContain(message.Contents, c => c is SourceContent);
            _ = Assert.Single(message.Contents.OfType<TextContent>());
        }

        [Fact]
        public async Task GetStreamingResponseAsync_StripsSourceContentBeforeTheInnerClientSeesIt()
        {
            SequencedChatClient inner = new("ok");
            ModelFacingChatClient client = new(inner);
            ChatMessage cited = new(ChatRole.Assistant,
            [
                new TextContent("here you go"),
                Citation,
            ]);

            await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
                [cited], cancellationToken: TestContext.Current.CancellationToken))
            {
            }

            List<ChatMessage> forwarded = Assert.Single(inner.Requests);
            ChatMessage message = Assert.Single(forwarded);
            Assert.DoesNotContain(message.Contents, c => c is SourceContent);
        }

        [Fact]
        public async Task GetResponseAsync_PassesAMessageWithNoSourceContentThroughByReference()
        {
            SequencedChatClient inner = new("ok");
            ModelFacingChatClient client = new(inner);
            ChatMessage plain = new(ChatRole.User, "hello");

            _ = await client.GetResponseAsync([plain], cancellationToken: TestContext.Current.CancellationToken);

            List<ChatMessage> forwarded = Assert.Single(inner.Requests);
            Assert.Same(plain, Assert.Single(forwarded));
        }

        [Fact]
        public async Task GetStreamingResponseAsync_PassesAMessageWithNoSourceContentThroughByReference()
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
            ChatMessage cited = new(ChatRole.Assistant,
            [
                Citation,
            ])
            {
                MessageId = "msg-1",
                AuthorName = "assistant-1",
                CreatedAt = DateTimeOffset.UnixEpoch,
                AdditionalProperties = new AdditionalPropertiesDictionary { ["k"] = "v" },
            };

            _ = await client.GetResponseAsync([cited], cancellationToken: TestContext.Current.CancellationToken);

            ChatMessage forwarded = Assert.Single(Assert.Single(inner.Requests));
            Assert.Equal("msg-1", forwarded.MessageId);
            Assert.Equal("assistant-1", forwarded.AuthorName);
            Assert.Equal(DateTimeOffset.UnixEpoch, forwarded.CreatedAt);
            Assert.Equal("v", forwarded.AdditionalProperties?["k"]);
        }
    }
}

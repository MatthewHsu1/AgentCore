using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// Pins <see cref="TranscriptJson.Options"/> against the one failure mode that matters: a column type
    /// that gives back an object's keys in a different order than they were written.
    /// </summary>
    public sealed class TranscriptJsonTests
    {
        [Fact]
        public void Options_RoundTripsAMessageWithOutOfOrderTypeDiscriminators()
        {
            // Arrange
            JsonElement data = JsonSerializer.SerializeToElement(new
            {
                orderId = "41",
                widget = new Dictionary<string, object?> { ["$type"] = "custom-widget", ["label"] = "Order #41" },
            });

            ChatMessage message = new(ChatRole.Assistant,
            [
                new TextContent("here's your order"),
                new FunctionCallContent("conversation-1", "lookup_order", new Dictionary<string, object?> { ["orderId"] = "41" }),
                new FunctionResultContent("conversation-1", new { status = "shipped" }),
                new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }),
                new RenderContent { Name = "order-card", RenderId = "order-41", Data = data },
            ]);

            string json = JsonSerializer.Serialize(message, TranscriptJson.Options);
            string shuffled = MoveTypeDiscriminatorsLast(JsonNode.Parse(json)!).ToJsonString();

            // Act
            ChatMessage result = JsonSerializer.Deserialize<ChatMessage>(shuffled, TranscriptJson.Options)!;

            // Assert
            Assert.Equal(5, result.Contents.Count);

            RenderContent render = Assert.Single(result.Contents.OfType<RenderContent>());
            Assert.Equal("order-card", render.Name);
            Assert.Equal("order-41", render.RenderId);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(data.GetRawText()), JsonNode.Parse(render.Data.GetRawText())));
        }

        /// <summary>Rewrites every object in the tree so a <c>$type</c> key, if present, comes back last.</summary>
        /// <remarks>
        /// <c>jsonb</c> sorts an object's keys, so a stored message's <c>$type</c> discriminator can come
        /// back anywhere. This is what a read out of that column actually looks like.
        /// </remarks>
        private static JsonNode MoveTypeDiscriminatorsLast(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    JsonObject reordered = [];
                    foreach (KeyValuePair<string, JsonNode?> property in obj.Where(property => property.Key != "$type"))
                    {
                        reordered[property.Key] = property.Value is null ? null : MoveTypeDiscriminatorsLast(property.Value.DeepClone());
                    }

                    if (obj.TryGetPropertyValue("$type", out JsonNode? typeValue))
                    {
                        reordered["$type"] = typeValue?.DeepClone();
                    }

                    return reordered;

                case JsonArray array:
                    IEnumerable<JsonNode?> items = array.Select(item => item is null ? null : MoveTypeDiscriminatorsLast(item.DeepClone()));
                    return new JsonArray([.. items]);

                default:
                    return node.DeepClone();
            }
        }
    }
}

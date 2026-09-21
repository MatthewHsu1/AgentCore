using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Transcript;
using AgentCore.Domain.Sources;
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
            // Arrange — the nested "$type" key inside the tool result is unrelated user data, and must
            // survive reordering untouched, alongside the outer array's own discriminators.
            ChatMessage message = new(ChatRole.Assistant,
            [
                new TextContent("here's your order"),
                new FunctionCallContent("conversation-1", "lookup_order", new Dictionary<string, object?> { ["orderId"] = "41" }),
                new FunctionResultContent("conversation-1", new
                {
                    status = "shipped",
                    widget = new Dictionary<string, object?> { ["$type"] = "custom-widget", ["label"] = "Order #41" },
                }),
                new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }),
                new SourceContent
                {
                    Source = new SourceReference { SourceId = "order-41", Kind = SourceKind.Document, Title = "Order #41", Origin = "knowledge" },
                    CallId = "conversation-1",
                },
            ]);

            string json = JsonSerializer.Serialize(message, TranscriptJson.Options);
            string shuffled = MoveTypeDiscriminatorsLast(JsonNode.Parse(json)!).ToJsonString();

            // Act
            ChatMessage result = JsonSerializer.Deserialize<ChatMessage>(shuffled, TranscriptJson.Options)!;

            // Assert
            Assert.Equal(5, result.Contents.Count);

            SourceContent source = Assert.Single(result.Contents.OfType<SourceContent>());
            Assert.Equal("order-41", source.Source.SourceId);
            Assert.Equal("conversation-1", source.CallId);

            FunctionResultContent tool = Assert.Single(result.Contents.OfType<FunctionResultContent>());
            JsonNode? toolResult = JsonSerializer.SerializeToNode(tool.Result);
            Assert.Equal("shipped", toolResult!["status"]!.GetValue<string>());
            Assert.Equal("custom-widget", toolResult["widget"]!["$type"]!.GetValue<string>());
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

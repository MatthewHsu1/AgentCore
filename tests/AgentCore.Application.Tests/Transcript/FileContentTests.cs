using System.Text.Json;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>A kept file survives the trip through the transcript store.</summary>
    public sealed class FileContentTests
    {
        [Fact]
        public void Kept_SurvivesTheTranscriptEncoder()
        {
            // Arrange: what the publish tool wrote, stored, read back.
            FileContent file = new()
            {
                Name = "chart.png",
                FileId = "out/chart.png",
                Title = "Sales by month",
                MediaType = "image/png",
                Length = 48213,
                Kept = true,
            };
            ChatMessage reply = new(ChatRole.Assistant, [new TextContent("see"), file]);

            // Act
            string json = JsonSerializer.Serialize(reply, TranscriptJson.Options);
            ChatMessage restored = JsonSerializer.Deserialize<ChatMessage>(json, TranscriptJson.Options)!;

            // Assert
            FileContent read = Assert.Single(restored.Contents.OfType<FileContent>());
            Assert.Equal(("chart.png", "out/chart.png", "Sales by month", "image/png", 48213L, true), (read.Name, read.FileId, read.Title, read.MediaType, read.Length, read.Kept));
            Assert.Contains("\"agentcore.file\"", json, StringComparison.Ordinal);
        }
    }
}

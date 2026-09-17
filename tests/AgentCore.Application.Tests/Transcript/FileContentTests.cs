using System.Text.Json;
using AgentCore.Application.Blobs;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>A kept file survives the trip through the transcript store.</summary>
public sealed class FileContentTests
{
    [Fact]
    public void Kept_SurvivesTheTranscriptEncoder()
    {
        // Arrange: what the capture wrote, stored, read back.
        FileContent file = new()
        {
            Name = "chart.png",
            FileId = "cfile_1",
            Scope = "cntr_1",
            MediaType = "image/png",
            Length = 48213,
            Kept = true,
        };
        ChatMessage reply = new(ChatRole.Assistant, [new TextContent("see"), file]);

        // Act
        var json = JsonSerializer.Serialize(reply, TranscriptJson.Options);
        var restored = JsonSerializer.Deserialize<ChatMessage>(json, TranscriptJson.Options)!;

        // Assert
        var read = Assert.Single(restored.Contents.OfType<FileContent>());
        Assert.Equal(("chart.png", "cfile_1", "cntr_1", "image/png", 48213L, true), (read.Name, read.FileId, read.Scope, read.MediaType, read.Length, read.Kept));
        Assert.Contains("\"agentcore.file\"", json, StringComparison.Ordinal);
    }
}

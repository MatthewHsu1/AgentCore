#pragma warning disable MEAI001

using System.Text.Json;
using AgentCore.Application.Blobs;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Blobs;

/// <summary>Which names a message's sandbox file references resolve to.</summary>
public sealed class SandboxFilesTests
{
    [Fact]
    public void NamesIn_FileOnTheMessageAndInsideAnInterpreterResult_ReturnsBothInOrder()
    {
        // Arrange
        AIContent[] contents =
        [
            new TextContent("see the chart"),
            new HostedFileContent("cfile_1") { Name = "chart.png" },
            new CodeInterpreterToolResultContent("call_ci") { Outputs = [new HostedFileContent("cfile_2") { Name = "rows.csv" }] },
        ];

        // Act
        var names = SandboxFiles.NamesIn(contents);

        // Assert
        Assert.Equal(["chart.png", "rows.csv"], names);
    }

    [Fact]
    public void NamesIn_SameFileTwice_ReturnsItOnce()
    {
        // Arrange
        AIContent[] contents =
        [
            new HostedFileContent("cfile_1") { Name = "chart.png" },
            new HostedFileContent("cfile_1") { Name = "chart.png" },
        ];

        // Act
        var names = SandboxFiles.NamesIn(contents);

        // Assert
        Assert.Equal(["chart.png"], names);
    }

    [Fact]
    public void NamesIn_NoName_FallsBackToTheVendorId()
    {
        // Act
        var names = SandboxFiles.NamesIn([new HostedFileContent("cfile_abc")]);

        // Assert
        Assert.Equal(["cfile_abc"], names);
    }

    [Fact]
    public void KeptIn_StampSurvivesTheTranscriptEncoder()
    {
        // Arrange: stamp a fresh reference, store the message, read it back.
        HostedFileContent reference = new("cfile_1") { Name = "chart.png" };
        SandboxFiles.MarkKept(reference, new BlobRef("call-1", "chart.png", "image/png", 48213));
        ChatMessage reply = new(ChatRole.Assistant, [new TextContent("see"), reference]);

        var json = JsonSerializer.Serialize(reply, TranscriptJson.Options);
        var restored = JsonSerializer.Deserialize<ChatMessage>(json, TranscriptJson.Options)!;

        // Act
        var facts = SandboxFiles.KeptIn(restored.Contents);

        // Assert
        Assert.Equal([new SandboxFileFacts("chart.png", "image/png", 48213)], facts);
    }

    [Fact]
    public void KeptIn_UnstampedOrRefusedReference_IsNotKept()
    {
        // Arrange
        HostedFileContent unstamped = new("cfile_1") { Name = "chart.png", MediaType = "image/png" };
        HostedFileContent refused = new("cfile_2") { Name = "big.png" };
        SandboxFiles.MarkRefused(refused);

        // Act
        var facts = SandboxFiles.KeptIn([unstamped, refused]);

        // Assert
        Assert.Empty(facts);
    }

    [Fact]
    public void KeptIn_SameNameStampedTwice_TheLaterFactsWinInTheFirstPlace()
    {
        // Arrange: the model redrew chart.png after rows.csv; the store replaced the first.
        HostedFileContent first = new("cfile_1") { Name = "chart.png" };
        HostedFileContent rows = new("cfile_2") { Name = "rows.csv" };
        HostedFileContent second = new("cfile_3") { Name = "chart.png" };
        SandboxFiles.MarkKept(first, new BlobRef("call-1", "chart.png", "image/png", 10));
        SandboxFiles.MarkKept(rows, new BlobRef("call-1", "rows.csv", "text/csv", 5));
        SandboxFiles.MarkKept(second, new BlobRef("call-1", "chart.png", "image/png", 20));

        // Act
        var facts = SandboxFiles.KeptIn([first, rows, second]);

        // Assert
        Assert.Equal([new SandboxFileFacts("chart.png", "image/png", 20), new SandboxFileFacts("rows.csv", "text/csv", 5)], facts);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("a/b.png")]
    [InlineData("")]
    public void NamesIn_UnsafeName_IsDropped(string name)
    {
        // Act
        var names = SandboxFiles.NamesIn([new HostedFileContent("cfile_1") { Name = name }]);

        // Assert
        Assert.Empty(names);
    }
}

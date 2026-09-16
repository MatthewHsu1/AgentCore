using AgentCore.Application.Blobs;
using Xunit;

namespace AgentCore.Application.Tests.Blobs;

/// <summary>The name a blob is keyed by. A key, never a path.</summary>
public sealed class BlobNameTests
{
    [Theory]
    [InlineData("chart.png")]
    [InlineData("sales report.pdf")]
    [InlineData("q3-données.csv")]
    public void IsSafe_PlainName_Accepts(string name)
    {
        Assert.True(BlobName.IsSafe(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../etc/passwd")]
    [InlineData("..")]
    [InlineData("a/b.png")]
    [InlineData("a\\b.png")]
    [InlineData("/chart.png")]
    [InlineData("chart\0.png")]
    [InlineData("chart\n.png")]
    [InlineData("chart..png")]
    public void IsSafe_PathOrControlChars_Refuses(string? name)
    {
        Assert.False(BlobName.IsSafe(name));
    }

    [Fact]
    public void IsSafe_Over255Utf8Bytes_Refuses()
    {
        // Arrange: 252 ASCII bytes + ".png" = 256 bytes.
        var name = new string('a', 252) + ".png";

        // Act & Assert
        Assert.False(BlobName.IsSafe(name));
    }

    [Fact]
    public void IsSafe_Exactly255Utf8Bytes_Accepts()
    {
        // Arrange
        var name = new string('a', 251) + ".png";

        // Act & Assert
        Assert.True(BlobName.IsSafe(name));
    }

    [Fact]
    public void IsSafe_MultiByteCharsCountAsBytesNotChars()
    {
        // Arrange: "é" is 2 UTF-8 bytes, so 126 of them + ".png" = 256 bytes in 130 chars.
        var name = new string('é', 126) + ".png";

        // Act & Assert
        Assert.False(BlobName.IsSafe(name));
    }
}

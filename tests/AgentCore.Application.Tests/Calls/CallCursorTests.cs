using AgentCore.Application.Calls;
using Xunit;

namespace AgentCore.Application.Tests.Calls;

/// <summary>The opaque cursor a listing hands back and takes again.</summary>
public sealed class CallCursorTests
{
    [Fact]
    public void Encode_ThenDecode_ReturnsBothValues()
    {
        // Arrange
        DateTimeOffset sortAt = new(2026, 8, 30, 12, 34, 56, 789, TimeSpan.Zero);

        // Act
        var cursor = CallCursor.Encode(sortAt, "call-1");
        var decoded = CallCursor.TryDecode(cursor, out var readAt, out var readId);

        // Assert
        Assert.True(decoded);
        Assert.Equal(sortAt, readAt);
        Assert.Equal("call-1", readId);
    }

    [Fact]
    public void Encode_ACallIdHoldingTheSeparator_RoundTripsWhole()
    {
        // Arrange
        const string CallId = "call|with|pipes";

        // Act
        var cursor = CallCursor.Encode(DateTimeOffset.UnixEpoch, CallId);
        CallCursor.TryDecode(cursor, out _, out var readId);

        // Assert
        Assert.Equal(CallId, readId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!")]
    [InlineData("bm90LWEtY3Vyc29y")]
    public void TryDecode_SomethingThatIsNotACursor_IsFalseAndNotAThrow(string? cursor)
    {
        // Act
        var decoded = CallCursor.TryDecode(cursor, out _, out _);

        // Assert
        Assert.False(decoded);
    }
}

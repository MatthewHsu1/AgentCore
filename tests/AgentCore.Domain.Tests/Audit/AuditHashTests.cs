using AgentCore.Domain.Audit;
using Xunit;

namespace AgentCore.Domain.Tests.Audit;

/// <summary>
/// The digest the chain stores in place of the words a caller said or heard.
/// </summary>
/// <remarks>
/// The words live in store 1 and stay erasable, so the chain must be able to prove a text it does
/// not hold. Both halves of that proof read the same bytes: this type, and
/// <c>encode(sha256(convert_to(t, 'UTF8')), 'hex')</c> in PostgreSQL.
/// </remarks>
public sealed class AuditHashTests
{
    [Theory]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    // The empty string is here on purpose: a turn that spoke nothing still writes 64 characters,
    // and an empty VALUE is the one thing the chain refuses on a stored kind.
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void OfText_AKnownVector_IsTheSha256OfItsUtf8Bytes(string text, string expected)
    {
        AuditHash hash = AuditHash.OfText(text);

        Assert.Equal(expected, hash.Value);
    }

    [Fact]
    public void OfText_TextOutsideTheBasicMultilingualPlane_ReadsItsUtf8Bytes()
    {
        // PostgreSQL hashes the UTF-8 bytes. .NET strings are UTF-16, and a surrogate pair is where
        // the two spellings would diverge if this read characters instead.
        AuditHash hash = AuditHash.OfText("\U0001F600");

        Assert.Equal("f0443a342c5ef54783a111b51ba56c938e474c32324d90c3a60c9c8e3a37e2d9", hash.Value);
    }
}

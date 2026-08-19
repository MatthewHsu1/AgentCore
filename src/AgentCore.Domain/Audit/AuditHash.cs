using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace AgentCore.Domain.Audit;

/// <summary>
/// One SHA-256 digest of the audit chain, as 64 lowercase hexadecimal characters.
/// </summary>
public sealed record AuditHash
{
    /// <summary>The number of characters in a hexadecimal SHA-256 digest.</summary>
    public const int Length = 64;

    private AuditHash(string value) => Value = value;

    /// <summary>Gets the 64 lowercase hexadecimal characters of the digest.</summary>
    public string Value { get; }

    /// <summary>Reads a hash from its hexadecimal text.</summary>
    /// <param name="value">64 lowercase hexadecimal characters.</param>
    /// <returns>The hash.</returns>
    /// <exception cref="ArgumentException">The text is not 64 lowercase hexadecimal characters.</exception>
    public static AuditHash Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryParse(value, out AuditHash? hash))
        {
            throw new ArgumentException(
                $"An audit hash is {Length} lowercase hexadecimal characters. This value is '{value}'.",
                nameof(value));
        }

        return hash;
    }

    /// <summary>Reads a hash from its hexadecimal text, and reports a bad shape instead of throwing.</summary>
    /// <param name="value">The text to read.</param>
    /// <param name="hash">The hash, when the text has the right shape.</param>
    /// <returns><see langword="true"/> when the text is 64 lowercase hexadecimal characters.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out AuditHash? hash)
    {
        hash = null;
        if (value is null || value.Length != Length)
        {
            return false;
        }

        foreach (char character in value)
        {
            bool isHexDigit = character is (>= '0' and <= '9') or (>= 'a' and <= 'f');
            if (!isHexDigit)
            {
                return false;
            }
        }

        hash = new AuditHash(value);
        return true;
    }

    /// <summary>Builds a hash from the 32 bytes of a digest.</summary>
    /// <param name="digest">The 32 bytes SHA-256 produced.</param>
    /// <returns>The hash.</returns>
    /// <exception cref="ArgumentException">The digest is not 32 bytes long.</exception>
    public static AuditHash FromDigest(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != Length / 2)
        {
            throw new ArgumentException(
                $"A SHA-256 digest is {Length / 2} bytes. This one is {digest.Length}.",
                nameof(digest));
        }

        return new AuditHash(Convert.ToHexStringLower(digest));
    }

    /// <summary>Hashes one text, so the chain can prove words it does not hold.</summary>
    /// <param name="text">The text to hash. The empty string is legal and hashes to a full digest.</param>
    /// <returns>The SHA-256 of the UTF-8 bytes of the text.</returns>
    /// <remarks>
    /// The spoken words of a call live in store 1, where they stay erasable, and the chain stores
    /// this instead. The bytes are UTF-8 with no byte-order mark, which is the same input
    /// <c>encode(sha256(convert_to(t, 'UTF8')), 'hex')</c> reads in PostgreSQL, so a reviewer can
    /// recompute the proof in either place.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static AuditHash OfText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, digest);
        return FromDigest(digest);
    }

    /// <summary>Reads the hexadecimal text of the hash.</summary>
    /// <returns>The 64 characters.</returns>
    public override string ToString() => Value;
}

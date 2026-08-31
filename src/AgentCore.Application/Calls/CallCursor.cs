using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace AgentCore.Application.Calls;

/// <summary>
/// The place in a listing that a page ended, as one opaque string.
/// </summary>
public static class CallCursor
{
    private const char Separator = '|';

    /// <summary>Encodes where a page ended.</summary>
    /// <param name="sortAt">The last row's sort time.</param>
    /// <param name="callId">The last row's call id.</param>
    /// <returns>A string the caller hands back unread.</returns>
    /// <exception cref="ArgumentNullException">The call id is <see langword="null"/>.</exception>
    public static string Encode(DateTimeOffset sortAt, string callId)
    {
        ArgumentNullException.ThrowIfNull(callId);

        var plain = string.Create(
            CultureInfo.InvariantCulture,
            $"{sortAt.UtcTicks}{Separator}{callId}");

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
    }

    /// <summary>Reads a cursor this class wrote.</summary>
    /// <param name="cursor">The value a caller handed back, which may be anything at all.</param>
    /// <param name="sortAt">The sort time it held, or <see langword="default"/>.</param>
    /// <param name="callId">The call id it held, or an empty string.</param>
    /// <returns><see langword="true"/> when the value was a cursor.</returns>
    public static bool TryDecode(string? cursor, out DateTimeOffset sortAt, out string callId)
    {
        sortAt = default;

        callId = string.Empty;

        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        Span<byte> bytes = new byte[Base64.GetMaxDecodedFromUtf8Length(cursor.Length)];

        if (!Convert.TryFromBase64String(cursor, bytes, out var written))
        {
            return false;
        }

        var plain = Encoding.UTF8.GetString(bytes[..written]);
        var split = plain.IndexOf(Separator, StringComparison.Ordinal);

        if (split <= 0 || split == plain.Length - 1)
        {
            return false;
        }

        if (!long.TryParse(plain[..split], CultureInfo.InvariantCulture, out var ticks)
            || ticks < 0
            || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        sortAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        callId = plain[(split + 1)..];

        return true;
    }
}

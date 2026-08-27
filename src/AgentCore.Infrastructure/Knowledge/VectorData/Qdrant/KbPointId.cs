using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;

/// <summary>
/// The point key of one card: <c>uuid5(namespace, prefix + card_id)</c>.
/// </summary>
internal static class KbPointId
{
    /// <summary>The prefix <c>kb sync</c> puts in front of a card id.</summary>
    public const string DefaultPrefix = "kb:";

    private static readonly Guid DnsNamespace = new("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
    private static readonly Guid UrlNamespace = new("6ba7b811-9dad-11d1-80b4-00c04fd430c8");
    private static readonly Guid OidNamespace = new("6ba7b812-9dad-11d1-80b4-00c04fd430c8");
    private static readonly Guid X500Namespace = new("6ba7b814-9dad-11d1-80b4-00c04fd430c8");

    /// <summary>Reads the namespace <c>links.namespace</c> names.</summary>
    public static Guid Namespace(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.ToLowerInvariant() switch
        {
            "url" => UrlNamespace,
            "dns" => DnsNamespace,
            "oid" => OidNamespace,
            "x500" => X500Namespace,
            _ => Guid.Parse(name),
        };
    }

    /// <summary>Builds the point key of one card, under the default namespace and prefix.</summary>
    public static Guid For(string cardId) => For(cardId, UrlNamespace, DefaultPrefix);

    /// <summary>Builds the point key of one card.</summary>
    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 4122 version 5 specifies SHA-1. This is an identifier, not a security boundary.")]
    public static Guid For(string cardId, Guid @namespace, string prefix)
    {
        ArgumentNullException.ThrowIfNull(cardId);
        ArgumentNullException.ThrowIfNull(prefix);

        var name = Encoding.UTF8.GetBytes(prefix + cardId);
        var hash = SHA1.HashData([.. @namespace.ToByteArray(bigEndian: true), .. name]);

        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }
}

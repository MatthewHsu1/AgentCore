using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;

/// <summary>
/// The RFC 4122 version 5 point key of one card: <c>uuid5(namespace, prefix + id)</c>.
/// </summary>
/// <remarks>
/// Only <c>links.lookup: uuid5</c> reaches this. Both the namespace and the prefix come from the
/// document, because they belong to whichever ingester built the collection: this class knows the
/// formula, never one deployment's parameters for it.
/// </remarks>
internal static class Uuid5PointId
{
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

    /// <summary>Builds the point key of one card.</summary>
    /// <param name="cardId">The id the collection stores at <c>fields.id</c>.</param>
    /// <param name="namespace">The uuid5 namespace <c>links.namespace</c> resolved to.</param>
    /// <param name="prefix">What <c>links.prefix</c> puts in front of the id. May be empty.</param>
    /// <returns>The key.</returns>
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

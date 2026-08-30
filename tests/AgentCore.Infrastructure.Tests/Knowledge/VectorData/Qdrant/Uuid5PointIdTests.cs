using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>
/// The uuid5 formula, and nothing about any one collection.
/// </summary>
/// <remarks>
/// Every fact here passes its own namespace and its own prefix. The class used to carry a default
/// pair — the URL namespace and the literal <c>"kb:"</c> — so half these tests read as if one
/// ingester's parameters were the formula's own. They are the caller's, always.
/// </remarks>
public sealed class Uuid5PointIdTests
{
    private const string Card = "widget-a17-drive-fault";

    [Theory]
    // Pinned literals, produced by an independent implementation:
    //   python -c "import uuid; print(uuid.uuid5(uuid.NAMESPACE_URL, PREFIX + CARD))"
    // A formula checked against itself proves nothing, and a mismatch here means every uuid5
    // lookup silently misses.
    [InlineData("url", "", "459e76aa-7b3f-59aa-aef2-da90c566e47c")]
    [InlineData("url", "kb:", "b0a8762f-3b73-5efe-8ce0-c889e5a37e20")]
    [InlineData("dns", "kb:", "6fe7cd5c-3e48-53bc-ad5a-ac16514fc07b")]
    public void For_MatchesAnIndependentUuid5(string ns, string prefix, string expected)
        => Assert.Equal(Guid.Parse(expected), Uuid5PointId.For(Card, Uuid5PointId.Namespace(ns), prefix));

    [Fact]
    public void For_IsDeterministic()
        => Assert.Equal(Key(Card), Key(Card));

    [Theory]
    [InlineData("widget-a17-drive-faul")]
    [InlineData("widget-a17-drive-faults")]
    [InlineData("WIDGET-A17-DRIVE-FAULT")]
    public void For_NearMissId_IsADifferentGuid(string near)
        => Assert.NotEqual(Key(Card), Key(near));

    [Fact]
    public void For_IsVersion5()
    {
        var bytes = Key(Card).ToByteArray(bigEndian: true);

        Assert.Equal(0x50, bytes[6] & 0xF0);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }

    [Fact]
    public void For_DifferentPrefix_IsADifferentGuid()
        => Assert.NotEqual(
            Uuid5PointId.For(Card, Uuid5PointId.Namespace("url"), "kb:"),
            Uuid5PointId.For(Card, Uuid5PointId.Namespace("url"), "doc:"));

    [Fact]
    public void For_AnEmptyPrefix_IsADifferentGuidFromAnyPrefix()
    {
        // The framework's own default is now the empty prefix, so this is the pair a deployment
        // gets wrong first: an ingester that hashed with a prefix and a document that names none.
        Assert.NotEqual(
            Uuid5PointId.For(Card, Uuid5PointId.Namespace("url"), string.Empty),
            Uuid5PointId.For(Card, Uuid5PointId.Namespace("url"), "kb:"));
    }

    [Fact]
    public void For_DifferentNamespace_IsADifferentGuid()
        => Assert.NotEqual(
            Uuid5PointId.For(Card, Uuid5PointId.Namespace("url"), "kb:"),
            Uuid5PointId.For(Card, Uuid5PointId.Namespace("dns"), "kb:"));

    [Theory]
    [InlineData("url", "6ba7b811-9dad-11d1-80b4-00c04fd430c8")]
    [InlineData("URL", "6ba7b811-9dad-11d1-80b4-00c04fd430c8")]
    [InlineData("dns", "6ba7b810-9dad-11d1-80b4-00c04fd430c8")]
    [InlineData("oid", "6ba7b812-9dad-11d1-80b4-00c04fd430c8")]
    [InlineData("x500", "6ba7b814-9dad-11d1-80b4-00c04fd430c8")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public void Namespace_ResolvesNameOrGuid(string name, string expected)
        => Assert.Equal(Guid.Parse(expected), Uuid5PointId.Namespace(name));

    [Fact]
    public void Namespace_UnknownName_Throws()
        => Assert.Throws<FormatException>(() => Uuid5PointId.Namespace("not-a-namespace"));

    private static Guid Key(string cardId)
        => Uuid5PointId.For(cardId, Uuid5PointId.Namespace("url"), "kb:");
}

using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

public sealed class KbPointIdTests
{
    [Theory]
    // Produced by the knowledge repository's own CLI:
    //   python -c "import uuid; print(uuid.uuid5(uuid.NAMESPACE_URL, 'kb:' + CARD))"
    // A mismatch means every lookup silently misses, so these are pinned literals, never
    // recomputed by the test.
    [InlineData("ct900-e33-incline-err", "dcf22fb0-da8d-5e7c-a652-f3ce51603c72")]
    public void For_MatchesPythonUuid5(string cardId, string expected)
        => Assert.Equal(Guid.Parse(expected), KbPointId.For(cardId));

    [Fact]
    public void For_IsDeterministic()
        => Assert.Equal(KbPointId.For("ct900-e33-incline-err"), KbPointId.For("ct900-e33-incline-err"));

    [Theory]
    [InlineData("ct900-e33-incline-er")]
    [InlineData("ct900-e33-incline-errs")]
    [InlineData("CT900-E33-INCLINE-ERR")]
    public void For_NearMissId_IsADifferentGuid(string near)
        => Assert.NotEqual(KbPointId.For("ct900-e33-incline-err"), KbPointId.For(near));

    [Fact]
    public void For_IsVersion5()
    {
        var bytes = KbPointId.For("ct900-e33-incline-err").ToByteArray(bigEndian: true);

        Assert.Equal(0x50, bytes[6] & 0xF0);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }

    [Theory]
    // Produced by the knowledge repository's own CLI, one per namespace:
    //   python -c "import uuid; print(uuid.uuid5(uuid.NAMESPACE_DNS, 'kb:ct900-e33-incline-err'))"
    // Pinned literals, never recomputed by the test: a formula that matches itself proves nothing.
    [InlineData("dns", "kb:", "0fe60d31-508c-5235-a2f7-029336c83bba")]
    [InlineData("url", "", "bc47173d-fa6d-5937-9f51-91fff6f7b77c")]
    public void For_WithNamespaceAndPrefix_MatchesPythonUuid5(string ns, string prefix, string expected)
        => Assert.Equal(
            Guid.Parse(expected),
            KbPointId.For("ct900-e33-incline-err", KbPointId.Namespace(ns), prefix));

    [Fact]
    public void For_DefaultOverload_EqualsUrlNamespaceWithKbPrefix()
        => Assert.Equal(
            KbPointId.For("ct900-e33-incline-err"),
            KbPointId.For("ct900-e33-incline-err", KbPointId.Namespace("url"), KbPointId.DefaultPrefix));

    [Fact]
    public void For_DifferentPrefix_IsADifferentGuid()
        => Assert.NotEqual(
            KbPointId.For("syn-00", KbPointId.Namespace("url"), "kb:"),
            KbPointId.For("syn-00", KbPointId.Namespace("url"), "doc:"));

    [Fact]
    public void For_DifferentNamespace_IsADifferentGuid()
        => Assert.NotEqual(
            KbPointId.For("syn-00", KbPointId.Namespace("url"), "kb:"),
            KbPointId.For("syn-00", KbPointId.Namespace("dns"), "kb:"));

    [Theory]
    [InlineData("url", "6ba7b811-9dad-11d1-80b4-00c04fd430c8")]
    [InlineData("URL", "6ba7b811-9dad-11d1-80b4-00c04fd430c8")]
    [InlineData("dns", "6ba7b810-9dad-11d1-80b4-00c04fd430c8")]
    [InlineData("oid", "6ba7b812-9dad-11d1-80b4-00c04fd430c8")]
    [InlineData("x500", "6ba7b814-9dad-11d1-80b4-00c04fd430c8")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public void Namespace_ResolvesNameOrGuid(string name, string expected)
        => Assert.Equal(Guid.Parse(expected), KbPointId.Namespace(name));

    [Fact]
    public void Namespace_UnknownName_Throws()
        => Assert.Throws<FormatException>(() => KbPointId.Namespace("not-a-namespace"));
}

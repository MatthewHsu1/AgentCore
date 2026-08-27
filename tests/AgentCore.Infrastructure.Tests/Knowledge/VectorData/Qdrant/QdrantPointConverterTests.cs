using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using Google.Protobuf.Collections;
using Qdrant.Client.Grpc;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

public sealed class QdrantPointConverterTests
{
    [Fact]
    public void ToPoint_ConvertsEveryValueKindWithoutALeakedProtobufType()
    {
        var payload = new MapField<string, Value>
        {
            ["s"] = "text",
            ["i"] = 3,
            ["d"] = new Value { DoubleValue = 0.5 },
            ["b"] = true,
            ["nested"] = new Value
            {
                StructValue = new Struct { Fields = { ["inner"] = new Value { StringValue = "deep" } } },
            },
            ["list"] = new Value
            {
                ListValue = new ListValue { Values = { new Value { StringValue = "a" }, new Value { IntegerValue = 2 } } },
            },
        };

        var point = QdrantPointConverter.ToPoint(
            new PointId { Uuid = "11111111-2222-3333-4444-555555555555" }, payload, score: 0.75);

        Assert.Equal("11111111-2222-3333-4444-555555555555", point.PointId);
        Assert.Equal(0.75, point.Score);
        Assert.Equal("text", point.Payload["s"]);
        Assert.Equal(3L, point.Payload["i"]);
        Assert.Equal(0.5, point.Payload["d"]);
        Assert.Equal(true, point.Payload["b"]);
        var nested = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(point.Payload["nested"]);
        Assert.Equal("deep", nested["inner"]);
        var list = Assert.IsAssignableFrom<IReadOnlyList<object?>>(point.Payload["list"]);
        Assert.Equal(["a", 2L], list);
    }

    [Fact]
    public void ToPoint_NumericPointKey_BecomesInvariantText()
    {
        var point = QdrantPointConverter.ToPoint(new PointId { Num = 42 }, [], score: null);

        Assert.Equal("42", point.PointId);
        Assert.Null(point.Score);
    }
}

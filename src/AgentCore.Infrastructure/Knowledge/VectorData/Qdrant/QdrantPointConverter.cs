using System.Globalization;
using AgentCore.Application.Knowledge;
using Google.Protobuf.Collections;
using Qdrant.Client.Grpc;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;

/// <summary>Reads one wire point into the vendor-free <see cref="KnowledgePoint"/>.</summary>
internal static class QdrantPointConverter
{
    public static KnowledgePoint ToPoint(PointId id, MapField<string, Value> payload, double? score) => new()
    {
        PointId = id.PointIdOptionsCase == PointId.PointIdOptionsOneofCase.Uuid
            ? id.Uuid
            : id.Num.ToString(CultureInfo.InvariantCulture),
        Score = score,
        Payload = Convert(payload),
    };

    private static Dictionary<string, object?> Convert(MapField<string, Value> fields)
        => fields.ToDictionary(entry => entry.Key, entry => Convert(entry.Value), StringComparer.Ordinal);

    private static object? Convert(Value value) => value.KindCase switch
    {
        Value.KindOneofCase.StringValue => value.StringValue,
        Value.KindOneofCase.IntegerValue => value.IntegerValue,
        Value.KindOneofCase.DoubleValue => value.DoubleValue,
        Value.KindOneofCase.BoolValue => value.BoolValue,
        Value.KindOneofCase.StructValue => Convert(value.StructValue.Fields),
        Value.KindOneofCase.ListValue => (IReadOnlyList<object?>)[.. value.ListValue.Values.Select(Convert)],
        _ => null,
    };
}

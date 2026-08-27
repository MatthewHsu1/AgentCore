using Google.Protobuf.Collections;
using Qdrant.Client.Grpc;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;

/// <summary>Reads a dotted-path field out of the nested payload struct <c>kb sync</c> writes.</summary>
internal static class QdrantPayload
{
    /// <summary>Walks a dotted path into a nested payload struct.</summary>
    public static Value? Read(MapField<string, Value> payload, string path)
    {
        Value? current = null;
        var fields = payload;

        foreach (var part in path.Split('.'))
        {
            if (fields is null || !fields.TryGetValue(part, out current))
            {
                return null;
            }

            fields = current.KindCase == Value.KindOneofCase.StructValue ? current.StructValue.Fields : null;
        }

        return current;
    }

    /// <summary>Reads a dotted path as a string, or empty when it is missing or not a string.</summary>
    public static string ReadString(MapField<string, Value> payload, string path) =>
        Read(payload, path)?.StringValue ?? string.Empty;

    /// <summary>Reads a dotted path as a list of strings, or empty when it is missing or not a list.</summary>
    public static List<string> ReadList(MapField<string, Value> payload, string path) =>
        Read(payload, path) is { KindCase: Value.KindOneofCase.ListValue } value
            ? [.. value.ListValue.Values.Select(item => item.StringValue)]
            : [];
}

using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Domain.Knowledge;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;

/// <summary>The built-in mapper: the <c>fields:</c> block, applied to one neutral point.</summary>
internal sealed class FieldsPointMapper : IKnowledgePointMapper
{
    /// <summary>The name the built-in mapping answers to.</summary>
    public const string MapperName = "fields";

    private readonly KnowledgeFieldsConfiguration _fields;

    public FieldsPointMapper(KnowledgeFieldsConfiguration fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        _fields = fields;
    }

    public string Name => MapperName;

    public KnowledgeCard? Map(KnowledgePoint point)
    {
        ArgumentNullException.ThrowIfNull(point);

        return new KnowledgeCard
        {
            CardId = ReadString(point.Payload, _fields.Id) is { Length: > 0 } id ? id : point.PointId,
            Text = ReadString(point.Payload, _fields.Body) ?? string.Empty,
            SourceRef = ReadString(point.Payload, _fields.Source) ?? string.Empty,
            SourceLocator = ReadString(point.Payload, _fields.Locator) ?? string.Empty,
            Authority = Read(point.Payload, _fields.Authority) is long authority ? (int)authority : null,
            Score = point.Score,
            ViaLink = false,
        };
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> payload, string? path)
        => Read(payload, path) as string;

    /// <summary>The neutral twin of <c>QdrantPayload.Read</c>: one dotted-path walk, null-tolerant.</summary>
    private static object? Read(IReadOnlyDictionary<string, object?> payload, string? path)
    {
        if (path is not { Length: > 0 })
        {
            return null;
        }

        object? current = null;
        var fields = payload;
        foreach (var part in path.Split('.'))
        {
            if (fields is null || !fields.TryGetValue(part, out current))
            {
                return null;
            }

            fields = current as IReadOnlyDictionary<string, object?>;
        }

        return current;
    }
}

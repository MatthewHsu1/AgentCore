using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Domain.Knowledge;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;

/// <summary>The built-in mapper: the <c>fields:</c> block, applied to one neutral point.</summary>
public sealed class FieldsPointMapper : IKnowledgePointMapper
{
    /// <summary>The name the built-in mapping answers to.</summary>
    public const string MapperName = "fields";

    private readonly KnowledgeFieldsConfiguration _fields;

    /// <summary>Creates the mapper over one <c>fields:</c> block.</summary>
    /// <param name="fields">The payload paths this collection's roles sit at.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is <see langword="null"/>.</exception>
    public FieldsPointMapper(KnowledgeFieldsConfiguration fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        _fields = fields;
    }

    /// <summary>Gets the name <c>providers.knowledge.mapper</c> selects this by.</summary>

    public string Name => MapperName;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="point"/> is <see langword="null"/>.</exception>
    public KnowledgeCard? Map(KnowledgePoint point)
    {
        ArgumentNullException.ThrowIfNull(point);

        return new KnowledgeCard
        {
            CardId = ReadString(point.Payload, _fields.Id) is { Length: > 0 } id ? id : point.PointId,
            Text = ReadString(point.Payload, _fields.Body) ?? string.Empty,
            SourceRef = ReadString(point.Payload, _fields.Source) ?? string.Empty,
            SourceLocator = ReadString(point.Payload, _fields.Locator) ?? string.Empty,
            Authority = PayloadPath.Read(point.Payload, _fields.Authority) is long authority ? (int)authority : null,
            Score = point.Score,
            ViaLink = false,

            // The whole payload, not the leftovers. The six roles above are what AgentCore acts on;
            // a deployment's own citation wording, or anything else reading a card downstream, needs
            // the fields this block never named, and there is no second round trip to fetch them.
            Extras = point.Payload,
        };
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> payload, string? path)
        => PayloadPath.Read(payload, path) as string;
}

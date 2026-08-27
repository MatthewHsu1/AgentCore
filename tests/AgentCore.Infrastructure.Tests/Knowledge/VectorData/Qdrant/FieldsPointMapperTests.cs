using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>
/// The built-in mapper is the <c>fields:</c> block applied to a neutral point. No Qdrant server:
/// the input is already vendor-free.
/// </summary>
public sealed class FieldsPointMapperTests
{
    [Fact]
    public void Map_DefaultFields_ReadsEveryRoleIncludingDottedPaths()
    {
        var mapper = new FieldsPointMapper(new KnowledgeFieldsConfiguration());
        var point = new KnowledgePoint
        {
            PointId = "11111111-2222-3333-4444-555555555555",
            Score = 0.5,
            Payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["card_id"] = "ct900-e33-incline-err",
                ["body"] = "err e33 incline motor error",
                ["authority"] = 3L,
                ["source"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ref"] = "ct900-om",
                    ["locator"] = "p.27",
                },
            },
        };

        var card = mapper.Map(point);

        Assert.NotNull(card);
        Assert.Equal("ct900-e33-incline-err", card!.CardId);
        Assert.Equal("err e33 incline motor error", card.Text);
        Assert.Equal("ct900-om", card.SourceRef);
        Assert.Equal("p.27", card.SourceLocator);
        Assert.Equal(3, card.Authority);
        Assert.Equal(0.5, card.Score);
        Assert.False(card.ViaLink);
    }

    [Fact]
    public void Map_IdFieldAbsentFromThePayload_FallsBackToThePointKey()
    {
        var mapper = new FieldsPointMapper(new KnowledgeFieldsConfiguration());
        var point = new KnowledgePoint
        {
            PointId = "point-key-7",
            Payload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["body"] = "text" },
        };

        var card = mapper.Map(point);

        Assert.Equal("point-key-7", card!.CardId);
        Assert.Equal(string.Empty, card.SourceRef);
        Assert.Equal(string.Empty, card.SourceLocator);
        Assert.Null(card.Authority);
    }

    [Fact]
    public void Map_NonStringBody_ReadsEmptyRatherThanThrowing()
    {
        var mapper = new FieldsPointMapper(new KnowledgeFieldsConfiguration());
        var point = new KnowledgePoint
        {
            PointId = "p",
            Payload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["body"] = 42L },
        };

        Assert.Equal(string.Empty, mapper.Map(point)!.Text);
    }

    [Fact]
    public void Name_IsFields()
        => Assert.Equal("fields", new FieldsPointMapper(new KnowledgeFieldsConfiguration()).Name);
}

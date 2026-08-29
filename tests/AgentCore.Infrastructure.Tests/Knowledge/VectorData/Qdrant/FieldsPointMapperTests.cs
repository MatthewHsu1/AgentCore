using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>
/// The built-in mapper is the <c>fields:</c> block applied to a neutral point. No Qdrant server:
/// the input is already vendor-free.
/// </summary>
/// <remarks>
/// Every fixture below writes the block out. There is nothing to leave blank: an empty block maps
/// no role at all, which these tests prove last.
/// </remarks>
public sealed class FieldsPointMapperTests
{
    [Fact]
    public void Map_EveryRoleMapped_ReadsThemAllIncludingDottedPaths()
    {
        var mapper = new FieldsPointMapper(Mapped);
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
        var mapper = new FieldsPointMapper(Mapped);
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
        var mapper = new FieldsPointMapper(Mapped);
        var point = new KnowledgePoint
        {
            PointId = "p",
            Payload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["body"] = 42L },
        };

        Assert.Equal(string.Empty, mapper.Map(point)!.Text);
    }

    [Fact]
    public void Map_NothingMapped_ReadsNoRoleAtAll()
    {
        // The proof that no name is built in. The payload below carries every name the old defaults
        // used, and a block that names none of them reads none of them.
        var mapper = new FieldsPointMapper(new KnowledgeFieldsConfiguration());
        var point = new KnowledgePoint
        {
            PointId = "point-key-7",
            Score = 0.5,
            Payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["card_id"] = "ct900-e33-incline-err",
                ["body"] = "err e33 incline motor error",
                ["authority"] = 3L,
            },
        };

        var card = mapper.Map(point);

        Assert.Equal("point-key-7", card!.CardId);
        Assert.Equal(string.Empty, card.Text);
        Assert.Null(card.Authority);
    }

    [Fact]
    public void Name_IsFields()
        => Assert.Equal("fields", new FieldsPointMapper(Mapped).Name);

    [Fact]
    public void Map_AlwaysCarriesTheWholePayloadInExtras()
    {
        // The escape hatch. A collection carries fields the six roles do not name, and a card that
        // dropped them would force a second round trip to read one.
        var mapper = new FieldsPointMapper(Mapped);
        var point = new KnowledgePoint
        {
            PointId = "p",
            Payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["body"] = "text",
                ["revision"] = "rev C",
                ["effective_date"] = "2026-01-01",
            },
        };

        var card = mapper.Map(point)!;

        Assert.Equal("rev C", card.Extras["revision"]);
        Assert.Equal("2026-01-01", card.Extras["effective_date"]);
    }

    /// <summary>A block that names every role, written out because nothing fills one in.</summary>
    private static KnowledgeFieldsConfiguration Mapped => new()
    {
        Id = "card_id",
        Body = "body",
        Lexical = "text",
        Source = "source.ref",
        Locator = "source.locator",
        Authority = "authority",
    };
}

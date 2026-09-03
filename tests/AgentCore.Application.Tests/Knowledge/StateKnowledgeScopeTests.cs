using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.State;
using AgentCore.Domain.Knowledge;
using Xunit;

namespace AgentCore.Application.Tests.Knowledge;

public sealed class StateKnowledgeScopeTests
{
    private static readonly KnowledgeScopeConfiguration Configured = new()
    {
        Template = "facets.{key}",
        Wildcard = new() { Value = "*", Facets = ["brand", "applies_to"] },
        FromState = ["brand", "applies_to"],
    };

    private static StateDocument Document()
    {
        StateSlotConfiguration Slot(params string[] members) => new()
        {
            Type = StateSlotType.String,
            Writer = StateWriter.Extractor,
            EnumValues = [.. members.Select(m => (JsonNode)JsonValue.Create(m)!)],
        };

        return new StateDocument(new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "doc",
            State = new Dictionary<string, StateSlotConfiguration>(StringComparer.Ordinal)
            {
                ["brand"] = Slot("sole", "spirit"),
                ["applies_to"] = Slot("f63", "f80"),
            },
        });
    }

    [Fact]
    public void Compose_NothingKnown_IsAllWildcard()
    {
        var scope = StateKnowledgeScope.Compose(Document(), Configured, ambient: null);

        Assert.Equal("*", scope!.Facets["brand"]);
        Assert.Equal("*", scope.Facets["applies_to"]);
    }

    [Fact]
    public void Compose_BrandKnown_LeavesTheMachineWildcard()
    {
        var state = Document();
        state.TryWrite("brand", JsonValue.Create("sole"));

        var scope = StateKnowledgeScope.Compose(state, Configured, ambient: null);

        Assert.Equal("sole", scope!.Facets["brand"]);
        Assert.Equal("*", scope.Facets["applies_to"]);
    }

    [Fact]
    public void Compose_HostAlreadySetTheKey_DoesNotOverwriteIt()
    {
        var state = Document();
        state.TryWrite("brand", JsonValue.Create("sole"));
        KnowledgeScope ambient = new()
        {
            Facets = new Dictionary<string, string>(StringComparer.Ordinal) { ["brand"] = "spirit" },
        };

        var scope = StateKnowledgeScope.Compose(state, Configured, ambient);

        Assert.Equal("spirit", scope!.Facets["brand"]);
    }

    [Fact]
    public void Compose_NoFromState_ReturnsTheAmbientInstance()
    {
        KnowledgeScope ambient = new()
        {
            Facets = new Dictionary<string, string>(StringComparer.Ordinal) { ["brand"] = "sole" },
        };

        var scope = StateKnowledgeScope.Compose(
            Document(), Configured with { FromState = [] }, ambient);

        Assert.Same(ambient, scope);
    }

    [Fact]
    public void Compose_NoWildcard_ReturnsTheAmbientInstance()
    {
        var scope = StateKnowledgeScope.Compose(
            Document(), Configured with { Wildcard = null }, ambient: null);

        Assert.Null(scope);
    }

    [Fact]
    public void Compose_MarksEachFacetWithItsOrigin()
    {
        var state = Document();
        state.TryWrite("brand", JsonValue.Create("sole"));
        KnowledgeScope ambient = new()
        {
            Facets = new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "uk" },
        };

        var scope = StateKnowledgeScope.Compose(state, Configured, ambient)!;

        Assert.Equal(KnowledgeFacetOrigin.Host, scope.Origins["region"]);
        Assert.Equal(KnowledgeFacetOrigin.Extractor, scope.Origins["brand"]);
        Assert.Equal(KnowledgeFacetOrigin.Wildcard, scope.Origins["applies_to"]);
    }
}

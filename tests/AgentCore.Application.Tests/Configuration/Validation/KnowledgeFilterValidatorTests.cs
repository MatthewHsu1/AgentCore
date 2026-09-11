using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Validation;

/// <summary>
/// The <c>resolve</c> block a filterable facet may carry: it names the lookup the model runs to find
/// an exact value, so every part of it must point at something real.
/// </summary>
public sealed class KnowledgeFilterValidatorTests
{
    private const string Pointer = "/providers/knowledge/scope/filterable/1/resolve";

    [Fact]
    public void Resolve_ViaAnotherFilterableFacet_Passes()
    {
        var result = ConfigurationValidator.EvaluateStructure(Filterable(Lookup(), Model(Via("lookup"))));

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Resolve_ViaAKeyNobodyDeclared_Fails()
    {
        var result = ConfigurationValidator.EvaluateStructure(Filterable(Lookup(), Model(Via("family"))));

        var error = Assert.Single(result.Errors);
        Assert.Equal(Pointer + "/via/key", error.Pointer);
        Assert.Contains("family", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ViaItself_Fails()
    {
        var result = ConfigurationValidator.EvaluateStructure(Filterable(Lookup(), Model(Via("model"))));

        var error = Assert.Single(result.Errors);
        Assert.Equal(Pointer + "/via/key", error.Pointer);
    }

    [Fact]
    public void Resolve_ViaAFacetThatResolvesToo_Fails()
    {
        var lookup = Lookup() with
        {
            Resolve = new()
            {
                Via = new() { Key = "model", Value = "x" },
                Query = "q",
                Read = "r",
            },
        };

        var result = ConfigurationValidator.EvaluateStructure(Filterable(lookup, Model(Via("lookup"))));

        Assert.Contains(result.Errors, e => e.Pointer == Pointer + "/via/key");
    }

    [Theory]
    [InlineData("value", "/via/value")]
    [InlineData("query", "/query")]
    [InlineData("read", "/read")]
    public void Resolve_ABlankPart_Fails(string blank, string suffix)
    {
        var resolve = Via("lookup") with
        {
            Via = new() { Key = "lookup", Value = blank == "value" ? " " : "model-numbers" },
            Query = blank == "query" ? " " : "<product> model number",
            Read = blank == "read" ? " " : "the Tag column",
        };

        var result = ConfigurationValidator.EvaluateStructure(Filterable(Lookup(), Model(resolve)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(Pointer + suffix, error.Pointer);
    }

    private static KnowledgeFacetResolveConfiguration Via(string key) => new()
    {
        Via = new() { Key = key, Value = "model-numbers" },
        Query = "<product> model number",
        Read = "the Tag column of the row for the person's year",
    };

    private static KnowledgeFilterableFacetConfiguration Lookup() =>
        new() { Key = "lookup", Description = "The only value is model-numbers." };

    private static KnowledgeFilterableFacetConfiguration Model(KnowledgeFacetResolveConfiguration resolve) =>
        new() { Key = "model", Description = "One machine and its year, as one tag.", Resolve = resolve };

    private static AgentCoreConfiguration Filterable(params KnowledgeFilterableFacetConfiguration[] facets) => new()
    {
        ApiVersion = "agentcore/v1",
        Name = "doc",
        Providers = new()
        {
            Knowledge = new()
            {
                Kind = "qdrant",
                Collection = "kb",
                Fields = new() { Body = "text" },
                Scope = new() { Template = "facets.{key}", Filterable = facets },
            },
        },
    };
}

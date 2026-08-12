using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tests.Configuration;
using AgentCore.Application.Tests.Tools.Fakes;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// The first tool kind of section 8.1: <c>kind: builtin</c>, which AgentCore ships.
/// </summary>
/// <remarks>
/// The worked example names two of them, <c>knowledge.search</c> and <c>knowledge.read</c>, and both
/// call <see cref="IKnowledgePort"/>. A built-in tool returns an error result and does not throw.
/// </remarks>
public sealed class BuiltinToolTests
{
    private static readonly ToolConfiguration Search = new()
    {
        Id = "search_chunks",
        Kind = ToolKind.Builtin,
        Uses = BuiltinToolNames.KnowledgeSearch,
    };

    private static readonly ToolConfiguration Read = new()
    {
        Id = "read_doc",
        Kind = ToolKind.Builtin,
        Uses = BuiltinToolNames.KnowledgeRead,
    };

    // ---------------------------------------------------------------------------------------------
    // What the model reads.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void ADeclaredSchema_ReachesTheModelUnchanged()
    {
        var declared = JsonNode.Parse("""{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""");
        var tool = Search with { Parameters = declared, Description = "Find a passage." };

        var function = Assert.IsAssignableFrom<AIFunction>(Factory().Create(tool));

        Assert.Equal("search_chunks", function.Name);
        Assert.Equal("Find a passage.", function.Description);
        Assert.True(JsonNode.DeepEquals(declared, JsonNode.Parse(function.JsonSchema.GetRawText())));
    }

    [Fact]
    public void ADocumentThatDeclaresNoSchema_GetsTheBuiltInOne()
    {
        // The worked example writes { id: search_chunks, kind: builtin, uses: knowledge.search } and
        // no parameters:. A tool the model cannot fill is useless, so the built-in publishes the
        // shape it reads. A declared schema always wins over it.
        var function = Assert.IsAssignableFrom<AIFunction>(Factory().Create(Search));

        Assert.Contains("query", function.JsonSchema.GetRawText(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Calling.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task KnowledgeSearch_ReturnsRankedChunks()
    {
        MapKnowledgePort knowledge = new();
        knowledge.With("returns.md", "A refund takes five days.");

        var result = await CallAsync(Factory(knowledge).Create(Search), ("query", "refund"));

        Assert.Equal("refund", Assert.Single(knowledge.Queries));
        var chunks = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(result)["chunks"]);
        Assert.Equal("returns.md", chunks[0]!["documentId"]!.GetValue<string>());
        Assert.Equal(1.0, chunks[0]!["score"]!.GetValue<double>());
    }

    [Fact]
    public async Task KnowledgeSearch_PassesTheLimitTheModelAsksFor()
    {
        MapKnowledgePort knowledge = new();

        await CallAsync(Factory(knowledge).Create(Search), ("query", "refund"), ("limit", 3));

        Assert.Equal(3, Assert.Single(knowledge.Limits));
    }

    [Fact]
    public async Task KnowledgeSearch_WithNoQuery_ReturnsAnErrorResult()
    {
        var result = await CallAsync(Factory().Create(Search));

        AssertError(result, "search_chunks", "query");
    }

    [Fact]
    public async Task KnowledgeRead_ReturnsOneDocument()
    {
        MapKnowledgePort knowledge = new();
        knowledge.With("returns.md", "A refund takes five days.");

        var result = await CallAsync(Factory(knowledge).Create(Read), ("documentId", "returns.md"));

        var document = Assert.IsType<JsonObject>(result);
        Assert.Equal("returns.md", document["documentId"]!.GetValue<string>());
        Assert.Equal("A refund takes five days.", document["text"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------------------------------------
    // Section 8.7: a tool returns an error result and does not throw.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task KnowledgeRead_OfADocumentThatIsNotThere_ReturnsAnErrorResult()
    {
        var result = await CallAsync(Factory().Create(Read), ("documentId", "missing.md"));

        AssertError(result, "read_doc", "missing.md");
    }

    [Fact]
    public async Task AnAdapterThatThrows_BecomesAnErrorResult()
    {
        MapKnowledgePort knowledge = new() { Failure = new InvalidOperationException("the store is down") };

        var result = await CallAsync(Factory(knowledge).Create(Search), ("query", "refund"));

        AssertError(result, "search_chunks", "the store is down");
    }

    [Fact]
    public async Task ACallerThatHangsUp_CancelsAndDoesNotBecomeAnErrorResult()
    {
        // A cancelled turn is not a tool failure. The model never reads this result, so swallowing
        // the cancellation would keep a dead call running.
        var function = Assert.IsAssignableFrom<AIFunction>(Factory().Create(Search));
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await function.InvokeAsync(Arguments(("query", "refund")), source.Token));
    }

    // ---------------------------------------------------------------------------------------------
    // Binding the name.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void TheWorkedExample_BindsBothBuiltInNames()
    {
        var document = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);
        var factory = Factory();

        foreach (var tool in document.Tools.Where(tool => tool.Kind == ToolKind.Builtin))
        {
            Assert.NotNull(factory.Create(tool));
        }
    }

    [Fact]
    public void AUsesNameNobodyShips_FailsAtStartup()
    {
        var tool = Search with { Uses = "knowledge.summarise" };

        var failure = Assert.Throws<ConfigurationLoadException>(() => Factory().Create(tool));

        Assert.Equal(ConfigurationCheck.ReferenceResolution, failure.Check);
        Assert.Contains("knowledge.summarise", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuiltinFactory_ServesNoOtherKind()
    {
        Assert.Null(Factory().Create(new ToolConfiguration { Id = "other", Kind = ToolKind.Binding, Binds = "X" }));
        Assert.Null(Factory().Create(new ToolConfiguration { Id = "inner", Kind = ToolKind.Agent, Agent = "a" }));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------
    private static BuiltinToolFactory Factory(IKnowledgePort? knowledge = null)
        => new(knowledge ?? new MapKnowledgePort());

    private static AIFunctionArguments Arguments(params (string Name, object? Value)[] arguments)
    {
        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            values[argument.Name] = argument.Value;
        }

        return new AIFunctionArguments(values);
    }

    private static async Task<object?> CallAsync(AITool? tool, params (string Name, object? Value)[] arguments)
    {
        var function = Assert.IsAssignableFrom<AIFunction>(tool);
        return await function.InvokeAsync(Arguments(arguments), TestContext.Current.CancellationToken);
    }

    private static void AssertError(object? result, string toolId, string fragment)
    {
        var error = Assert.IsType<JsonObject>(result);
        Assert.True(ToolErrorResult.IsError(error));
        Assert.Equal(toolId, error["tool"]!.GetValue<string>());
        Assert.Contains(fragment, error["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}

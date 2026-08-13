using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
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
/// The worked example names four of them. <c>knowledge.search</c> calls
/// <see cref="IKnowledgeRetrievalPort"/>, and <c>knowledge.read</c>, <c>knowledge.list</c> and
/// <c>knowledge.grep</c> call <see cref="IDocumentStorePort"/>, so the two ports bind apart. A
/// built-in tool returns an error result and does not throw.
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

    private static readonly ToolConfiguration ListDocs = new()
    {
        Id = "list_docs",
        Kind = ToolKind.Builtin,
        Uses = BuiltinToolNames.KnowledgeList,
    };

    private static readonly ToolConfiguration GrepDocs = new()
    {
        Id = "grep_docs",
        Kind = ToolKind.Builtin,
        Uses = BuiltinToolNames.KnowledgeGrep,
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

    [Fact]
    public void KnowledgeList_ThatDeclaresNoSchema_PublishesItsOwnPattern()
    {
        var function = Assert.IsAssignableFrom<AIFunction>(Factory().Create(ListDocs));

        Assert.Contains("pattern", function.JsonSchema.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void KnowledgeGrep_ThatDeclaresNoSchema_PublishesItsOwnPatternAndGlob()
    {
        // The pattern is the one argument the model must fill, so the schema also says so.
        var function = Assert.IsAssignableFrom<AIFunction>(Factory().Create(GrepDocs));

        var schema = function.JsonSchema.GetRawText();
        Assert.Contains("pattern", schema, StringComparison.Ordinal);
        Assert.Contains("glob", schema, StringComparison.Ordinal);
        Assert.Contains("required", schema, StringComparison.Ordinal);
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

    [Fact]
    public async Task KnowledgeList_NamesEveryDocument()
    {
        MapKnowledgePort knowledge = new();
        knowledge.With("policies/returns.md", "A refund takes five days.");
        knowledge.With("faq.md", "We open at nine.");

        var result = await CallAsync(Factory(knowledge).Create(ListDocs));

        var listing = Assert.IsType<JsonObject>(result);
        string[] expected = ["faq.md", "policies/returns.md"];
        Assert.Equal(expected, DocumentIds(listing));
        Assert.False(listing["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task KnowledgeList_KeepsOnlyThePatternTheModelAsksFor()
    {
        MapKnowledgePort knowledge = new();
        knowledge.With("policies/returns.md", "A refund takes five days.");
        knowledge.With("faq.md", "We open at nine.");

        var result = await CallAsync(Factory(knowledge).Create(ListDocs), ("pattern", "policies/**/*.md"));

        string[] expected = ["policies/returns.md"];
        Assert.Equal(expected, DocumentIds(Assert.IsType<JsonObject>(result)));
    }

    [Fact]
    public async Task KnowledgeList_ThatTheCapCut_SaysSo()
    {
        // The store caps the answer, and the flag is what stops the model from reading a short list
        // as the whole tree. The tool carries the flag through and never invents it.
        MapKnowledgePort knowledge = new();
        for (var index = 0; index < 201; index++)
        {
            knowledge.With($"doc-{index:D3}.md", "A refund takes five days.");
        }

        var result = await CallAsync(Factory(knowledge).Create(ListDocs));

        var listing = Assert.IsType<JsonObject>(result);
        Assert.Equal(200, DocumentIds(listing).Count);
        Assert.True(listing["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task KnowledgeGrep_ReturnsTheMatchingLines()
    {
        MapKnowledgePort knowledge = new();
        knowledge.With("returns.md", "We take returns.\nA refund takes five days.");

        var result = await CallAsync(Factory(knowledge).Create(GrepDocs), ("pattern", "refund"));

        var found = Assert.IsType<JsonObject>(result);
        var match = Assert.Single(Assert.IsType<JsonArray>(found["matches"])!);
        Assert.Equal("returns.md", match!["documentId"]!.GetValue<string>());
        Assert.Equal(2, match["lineNumber"]!.GetValue<int>());
        Assert.Equal("A refund takes five days.", match["line"]!.GetValue<string>());
        Assert.False(found["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task KnowledgeGrep_ReadsOnlyTheGlobTheModelAsksFor()
    {
        MapKnowledgePort knowledge = new();
        knowledge.With("policies/returns.md", "A refund takes five days.");
        knowledge.With("faq.md", "A refund takes five days.");

        var result = await CallAsync(
            Factory(knowledge).Create(GrepDocs),
            ("pattern", "refund"),
            ("glob", "policies/**/*.md"));

        var match = Assert.Single(Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(result)["matches"])!);
        Assert.Equal("policies/returns.md", match!["documentId"]!.GetValue<string>());
    }

    [Fact]
    public async Task KnowledgeGrep_ThatTheCapCut_SaysSo()
    {
        MapKnowledgePort knowledge = new();
        knowledge.With("returns.md", string.Join('\n', Enumerable.Repeat("A refund takes five days.", 101)));

        var result = await CallAsync(Factory(knowledge).Create(GrepDocs), ("pattern", "refund"));

        var found = Assert.IsType<JsonObject>(result);
        Assert.Equal(100, Assert.IsType<JsonArray>(found["matches"])!.Count);
        Assert.True(found["truncated"]!.GetValue<bool>());
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
    public async Task KnowledgeGrep_WithNoPattern_ReturnsAnErrorResult()
    {
        // Row T46: a required argument the model leaves out arrives as a silent default, so the tool
        // checks the pattern itself. An empty pattern would otherwise match every line of every
        // document.
        var result = await CallAsync(Factory().Create(GrepDocs));

        AssertError(result, "grep_docs", "pattern");
    }

    [Fact]
    public async Task KnowledgeGrep_WithAnEmptyPattern_ReturnsAnErrorResult()
    {
        var result = await CallAsync(Factory().Create(GrepDocs), ("pattern", string.Empty));

        AssertError(result, "grep_docs", "pattern");
    }

    [Fact]
    public async Task KnowledgeGrep_WithAPatternThatIsNotARegex_ReturnsAnErrorResult()
    {
        // The model writes the pattern, so a pattern that will not parse is an ordinary bad argument
        // and not a defect. Section 8.7 says the model reads the failure and tries again.
        MapKnowledgePort knowledge = new();
        knowledge.With("returns.md", "A refund takes five days.");

        var result = await CallAsync(Factory(knowledge).Create(GrepDocs), ("pattern", "[unclosed"));

        AssertError(result, "grep_docs", "RegexParseException");
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
    public void TheWorkedExample_BindsEveryBuiltInName()
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

    // ---------------------------------------------------------------------------------------------
    // One port bound and one not. A vendor that supplies only search must not have to read files.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AHostThatBindsOnlyTheDocumentStore_StillGetsKnowledgeRead()
    {
        MapKnowledgePort documents = new();
        documents.With("returns.md", "A refund takes five days.");
        BuiltinToolFactory factory = new(retrieval: null, documents);

        var result = await CallAsync(factory.Create(Read), ("documentId", "returns.md"));

        Assert.Equal("returns.md", Assert.IsType<JsonObject>(result)["documentId"]!.GetValue<string>());
    }

    [Fact]
    public void AHostThatBindsOnlyTheDocumentStore_FailsTheLoadOnKnowledgeSearch()
    {
        // The failure lands while the document loads, and it names the port nothing binds. A tool
        // that went missing here would take a whole access path with it.
        BuiltinToolFactory factory = new(retrieval: null, new MapKnowledgePort());

        var failure = Assert.Throws<ConfigurationLoadException>(() => factory.Create(Search));

        Assert.Equal(ConfigurationCheck.ReferenceResolution, failure.Check);
        Assert.Contains("search_chunks", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IKnowledgeRetrievalPort), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatBindsOnlyRetrieval_StillGetsKnowledgeSearch()
    {
        MapKnowledgePort retrieval = new();
        retrieval.With("returns.md", "A refund takes five days.");
        BuiltinToolFactory factory = new(retrieval, documents: null);

        var result = await CallAsync(factory.Create(Search), ("query", "refund"));

        var chunks = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(result)["chunks"]);
        Assert.Equal("returns.md", chunks[0]!["documentId"]!.GetValue<string>());
    }

    [Fact]
    public void AHostThatBindsOnlyRetrieval_FailsTheLoadOnKnowledgeRead()
    {
        BuiltinToolFactory factory = new(new MapKnowledgePort(), documents: null);

        var failure = Assert.Throws<ConfigurationLoadException>(() => factory.Create(Read));

        Assert.Equal(ConfigurationCheck.ReferenceResolution, failure.Check);
        Assert.Contains("read_doc", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IDocumentStorePort), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostThatBindsOnlyRetrieval_FailsTheLoadOnKnowledgeList()
    {
        BuiltinToolFactory factory = new(new MapKnowledgePort(), documents: null);

        var failure = Assert.Throws<ConfigurationLoadException>(() => factory.Create(ListDocs));

        Assert.Equal(ConfigurationCheck.ReferenceResolution, failure.Check);
        Assert.Contains("list_docs", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IDocumentStorePort), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostThatBindsOnlyRetrieval_FailsTheLoadOnKnowledgeGrep()
    {
        BuiltinToolFactory factory = new(new MapKnowledgePort(), documents: null);

        var failure = Assert.Throws<ConfigurationLoadException>(() => factory.Create(GrepDocs));

        Assert.Equal(ConfigurationCheck.ReferenceResolution, failure.Check);
        Assert.Contains("grep_docs", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IDocumentStorePort), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatBindsOnlyTheDocumentStore_StillGetsKnowledgeListAndKnowledgeGrep()
    {
        MapKnowledgePort documents = new();
        documents.With("returns.md", "A refund takes five days.");
        BuiltinToolFactory factory = new(retrieval: null, documents);

        var listing = Assert.IsType<JsonObject>(await CallAsync(factory.Create(ListDocs)));
        var found = Assert.IsType<JsonObject>(await CallAsync(factory.Create(GrepDocs), ("pattern", "refund")));

        Assert.Equal("returns.md", Assert.Single(DocumentIds(listing)));
        Assert.Single(Assert.IsType<JsonArray>(found["matches"])!);
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
    private static BuiltinToolFactory Factory(MapKnowledgePort? knowledge = null)
    {
        var store = knowledge ?? new MapKnowledgePort();
        return new BuiltinToolFactory(store, store);
    }

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

    /// <summary>Reads the ids one <c>knowledge.list</c> result names.</summary>
    private static IReadOnlyList<string> DocumentIds(JsonObject listing)
        => [.. Assert.IsType<JsonArray>(listing["documentIds"])!.Select(id => id!.GetValue<string>())];

    private static void AssertError(object? result, string toolId, string fragment)
    {
        var error = Assert.IsType<JsonObject>(result);
        Assert.True(ToolErrorResult.IsError(error));
        Assert.Equal(toolId, error["tool"]!.GetValue<string>());
        Assert.Contains(fragment, error["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}

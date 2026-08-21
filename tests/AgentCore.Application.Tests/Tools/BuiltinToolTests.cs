using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Configuration;
using AgentCore.Application.Tests.Tools.Fakes;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Builtin;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// The first tool kind of section 8.1: <c>kind: builtin</c>, which AgentCore ships.
/// </summary>
/// <remarks>
/// <para>
/// The worked example names four of them. <c>knowledge.search</c> calls
/// <see cref="IKnowledgeRetrievalPort"/>, and <c>knowledge.read</c>, <c>knowledge.list</c> and
/// <c>knowledge.grep</c> call <see cref="IDocumentStorePort"/>, so the two ports bind apart. A
/// built-in tool answers a bad argument it can check for itself with an error result directly, and
/// leaves an adapter's own fault to propagate — Task 7a moved the classification of that fault to
/// <c>AuditingFunctionInvokingChatClient</c>, so a test that invokes the tool directly now sees it.
/// </para>
/// <para>
/// Task 7b made each built-in a plain C# method behind <see cref="AIFunctionFactory"/>, which moves
/// two things these tests pin. The schema the model reads is generated from the parameter list and
/// lives in <see cref="BuiltinToolSchemaTests"/>, which compares it whole. And an argument that is
/// ABSENT no longer reaches the tool body at all — binding throws before it — so the "no argument"
/// cases below propagate exactly as a bad regular expression already did, while an argument that is
/// PRESENT AND EMPTY still reaches the body and still answers with an error result, because row T46
/// says the empty string is what a model that meant to omit an argument actually sends.
/// </para>
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
    public void TheDocumentOwnsTheNameAndTheDescription()
    {
        // The C# method name is never advertised: the model calls the id the document chose, and
        // reads the sentence the document wrote to decide when to call it. The schema underneath is
        // generated, and BuiltinToolSchemaTests compares every one of the four whole.
        var tool = Search with { Description = "Find a passage." };

        var function = Assert.IsAssignableFrom<AIFunction>(Factory().Create(tool));

        Assert.Equal("search_chunks", function.Name);
        Assert.Equal("Find a passage.", function.Description);
    }

    [Fact]
    public void ABuiltIn_AdvertisesNoResultSchema()
    {
        // A built-in answers with either its result or the section 8.7 error object, so a generated
        // schema of the success shape alone would describe every failure as malformed. This is what
        // the tools advertised before Task 7b too, and ExcludeResultSchema is what keeps it.
        var function = Assert.IsAssignableFrom<AIFunction>(Factory().Create(Search));

        Assert.Null(function.ReturnJsonSchema);
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
    public async Task KnowledgeSearch_WithAnEmptyQuery_ReturnsAnErrorResult()
    {
        // Row T46: the empty string is what a model that meant to fill nothing usually sends, and it
        // reaches the body, so the body answers it.
        var result = await CallAsync(Factory().Create(Search), ("query", string.Empty));

        AssertError(result, "search_chunks", "query");
    }

    [Fact]
    public async Task KnowledgeSearch_WithNoQueryAtAll_PropagatesForTheMiddlewareToClassify()
    {
        // A call that omits the key entirely never reaches the body: AIFunctionFactory binds the
        // parameters first and throws. That is an ordinary bad argument, so
        // AuditingFunctionInvokingChatClient answers the model with the error result it reads and
        // can retry from — pinned end to end by
        // AuditingFunctionInvokingChatClientErrorPolicyTests.ARealBuiltinToolCalledWithNoArgumentAtAll_...
        var thrown = await Assert.ThrowsAsync<ArgumentException>(
            async () => await CallAsync(Factory().Create(Search)));

        Assert.Contains("query", thrown.Message, StringComparison.Ordinal);
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
    public async Task KnowledgeGrep_WithNoPatternAtAll_PropagatesForTheMiddlewareToClassify()
    {
        // See KnowledgeSearch_WithNoQueryAtAll_PropagatesForTheMiddlewareToClassify: an omitted key
        // is a binding failure and never reaches the body.
        var thrown = await Assert.ThrowsAsync<ArgumentException>(
            async () => await CallAsync(Factory().Create(GrepDocs)));

        Assert.Contains("pattern", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnowledgeGrep_WithAnEmptyPattern_ReturnsAnErrorResult()
    {
        // Row T46: a required argument the model leaves out often arrives as the empty string rather
        // than as a missing key, and binding cannot tell that apart from a real value. An empty
        // pattern matches every line of every document, so the tool checks it itself.
        var result = await CallAsync(Factory().Create(GrepDocs), ("pattern", string.Empty));

        AssertError(result, "grep_docs", "pattern");
    }

    [Fact]
    public async Task KnowledgeGrep_WithAPatternThatIsNotARegex_PropagatesForTheMiddlewareToClassify()
    {
        // The model writes the pattern, so a pattern that will not parse is an ordinary bad argument
        // and not a defect: section 8.7 says the model reads the failure and tries again. Task 7a
        // moved the classification that turns this into an error result out of DeclaredTool and into
        // AuditingFunctionInvokingChatClient, so calling the bare tool directly — as this test does —
        // now sees the exception. AuditingFunctionInvokingChatClientErrorPolicyTests and
        // CallSessionTests.ARealDeclaredToolWhoseFaultTheModelCanAnswer_CompletesTheTurnNormally pin
        // the end-to-end guarantee that this still becomes an error result the model reads.
        MapKnowledgePort knowledge = new();
        knowledge.With("returns.md", "A refund takes five days.");

        var thrown = await Assert.ThrowsAsync<System.Text.RegularExpressions.RegexParseException>(
            async () => await CallAsync(Factory(knowledge).Create(GrepDocs), ("pattern", "[unclosed")));

        Assert.Contains("Unterminated", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAdapterThatThrows_PropagatesForTheMiddlewareToClassify()
    {
        // See the remarks on KnowledgeGrep_WithAPatternThatIsNotARegex_PropagatesForTheMiddlewareToClassify:
        // the same move applies here.
        MapKnowledgePort knowledge = new() { Failure = new InvalidOperationException("the store is down") };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await CallAsync(Factory(knowledge).Create(Search), ("query", "refund")));

        Assert.Equal("the store is down", thrown.Message);
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
        BuiltinFactory factory = new(retrieval: null, documents);

        var result = await CallAsync(factory.Create(Read), ("documentId", "returns.md"));

        Assert.Equal("returns.md", Assert.IsType<JsonObject>(result)["documentId"]!.GetValue<string>());
    }

    [Fact]
    public void AHostThatBindsOnlyTheDocumentStore_FailsTheLoadOnKnowledgeSearch()
    {
        // The failure lands while the document loads, and it names the port nothing binds. A tool
        // that went missing here would take a whole access path with it.
        BuiltinFactory factory = new(retrieval: null, new MapKnowledgePort());

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
        BuiltinFactory factory = new(retrieval, documents: null);

        var result = await CallAsync(factory.Create(Search), ("query", "refund"));

        var chunks = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(result)["chunks"]);
        Assert.Equal("returns.md", chunks[0]!["documentId"]!.GetValue<string>());
    }

    [Fact]
    public void AHostThatBindsOnlyRetrieval_FailsTheLoadOnKnowledgeRead()
    {
        BuiltinFactory factory = new(new MapKnowledgePort(), documents: null);

        var failure = Assert.Throws<ConfigurationLoadException>(() => factory.Create(Read));

        Assert.Equal(ConfigurationCheck.ReferenceResolution, failure.Check);
        Assert.Contains("read_doc", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IDocumentStorePort), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostThatBindsOnlyRetrieval_FailsTheLoadOnKnowledgeList()
    {
        BuiltinFactory factory = new(new MapKnowledgePort(), documents: null);

        var failure = Assert.Throws<ConfigurationLoadException>(() => factory.Create(ListDocs));

        Assert.Equal(ConfigurationCheck.ReferenceResolution, failure.Check);
        Assert.Contains("list_docs", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IDocumentStorePort), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostThatBindsOnlyRetrieval_FailsTheLoadOnKnowledgeGrep()
    {
        BuiltinFactory factory = new(new MapKnowledgePort(), documents: null);

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
        BuiltinFactory factory = new(retrieval: null, documents);

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
    private static BuiltinFactory Factory(MapKnowledgePort? knowledge = null)
    {
        var store = knowledge ?? new MapKnowledgePort();
        return new BuiltinFactory(store, store);
    }

    /// <summary>Builds one declared tool through <see cref="BuiltinToolSource"/>, synchronously.</summary>
    private sealed class BuiltinFactory
    {
        private readonly BuiltinToolSource _source;

        // The chat client factory is always bound: ui.draw is a shipped agent, and one declared in
        // a document with no factory behind it fails the boot rather than building.
        public BuiltinFactory(IKnowledgeRetrievalPort? retrieval, IDocumentStorePort? documents)
            => _source = new BuiltinToolSource(
                new BuiltinToolPorts(retrieval, documents, new RecordingChatClientFactory()));

        public AITool? Create(ToolConfiguration tool)
        {
            if (tool.Kind != ToolKind.Builtin)
            {
                return null;
            }

            var context = new ToolSourceContext(new AgentCoreConfiguration
            {
                ApiVersion = "agentcore/v1",
                Name = "test",
                Tools = [tool],
            });

            var registrations = _source.ProvideAsync(context).AsTask().GetAwaiter().GetResult();
            return Assert.Single(registrations).Materialise();
        }
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

    /// <summary>Calls one tool and reads its result as a node tree.</summary>
    /// <remarks>
    /// <see cref="AIFunctionFactory"/> marshals whatever the method returned into a
    /// <see cref="JsonElement"/>, where the hand-written tools returned their <see cref="JsonObject"/>
    /// untouched. Nothing on the wire changes — <c>CallSession.ToNode</c> already reads either — so
    /// this helper carries the element back into the tree the assertions below read.
    /// </remarks>
    private static async Task<JsonNode?> CallAsync(AITool? tool, params (string Name, object? Value)[] arguments)
    {
        var function = Assert.IsAssignableFrom<AIFunction>(tool);
        var result = await function.InvokeAsync(Arguments(arguments), TestContext.Current.CancellationToken);

        return result switch
        {
            null => null,
            JsonNode node => node,
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            _ => JsonValue.Create(result.ToString()),
        };
    }

    /// <summary>Reads the ids one <c>knowledge.list</c> result names.</summary>
    private static IReadOnlyList<string> DocumentIds(JsonObject listing)
        => [.. Assert.IsType<JsonArray>(listing["documentIds"])!.Select(id => id!.GetValue<string>())];

    private static void AssertError(JsonNode? result, string toolId, string fragment)
    {
        var error = Assert.IsType<JsonObject>(result);
        Assert.True(ToolErrorResult.IsError(error));
        Assert.Equal(toolId, error["tool"]!.GetValue<string>());
        Assert.Contains(fragment, error["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // BuiltinToolSource: description resolution.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task ABuiltinWithNoDeclaredDescription_TakesTheShippedDefault()
    {
        BuiltinToolSource source = new(new BuiltinToolPorts(new MapKnowledgePort(), null, null));
        var context = new ToolSourceContext(new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Tools = [new ToolConfiguration { Id = "search_chunks", Kind = ToolKind.Builtin, Uses = "knowledge.search" }],
        });

        var registrations = await source.ProvideAsync(context, TestContext.Current.CancellationToken);

        Assert.NotEqual(string.Empty, Assert.Single(registrations).Description);
    }

    [Fact]
    public async Task ABuiltinWithADeclaredDescription_KeepsTheDocumentsWords()
    {
        const string Declared = "Find a passage.";
        BuiltinToolSource source = new(new BuiltinToolPorts(new MapKnowledgePort(), null, null));
        var context = new ToolSourceContext(new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Tools =
            [
                new ToolConfiguration
                {
                    Id = "search_chunks", Kind = ToolKind.Builtin, Uses = "knowledge.search", Description = Declared,
                },
            ],
        });

        var registrations = await source.ProvideAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(Declared, registrations[0].Description);
    }

    [Fact]
    public async Task ABuiltinWithNoDeclaredDescription_MaterialisesTheShippedDefault()
    {
        BuiltinToolSource source = new(new BuiltinToolPorts(new MapKnowledgePort(), null, null));
        var context = new ToolSourceContext(new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Tools = [new ToolConfiguration { Id = "search_chunks", Kind = ToolKind.Builtin, Uses = "knowledge.search" }],
        });

        var registrations = await source.ProvideAsync(context, TestContext.Current.CancellationToken);
        var registration = Assert.Single(registrations);
        var function = Assert.IsAssignableFrom<AIFunction>(registration.Materialise());

        Assert.Equal(new KnowledgeSearchDefinition().DefaultDescription, function.Description);
    }

    [Fact]
    public async Task ABuiltinWithADeclaredDescription_MaterialisesTheDocumentsWords()
    {
        const string Declared = "Find a passage.";
        BuiltinToolSource source = new(new BuiltinToolPorts(new MapKnowledgePort(), null, null));
        var context = new ToolSourceContext(new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Tools =
            [
                new ToolConfiguration
                {
                    Id = "search_chunks", Kind = ToolKind.Builtin, Uses = "knowledge.search", Description = Declared,
                },
            ],
        });

        var registrations = await source.ProvideAsync(context, TestContext.Current.CancellationToken);
        var registration = Assert.Single(registrations);
        var function = Assert.IsAssignableFrom<AIFunction>(registration.Materialise());

        Assert.Equal(Declared, function.Description);
    }

    [Fact]
    public async Task ABuiltinWhosePortIsUnbound_FailsTheBoot()
    {
        BuiltinToolSource source = new(new BuiltinToolPorts(null, null, null));
        var context = new ToolSourceContext(new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Tools = [new ToolConfiguration { Id = "search_chunks", Kind = ToolKind.Builtin, Uses = "knowledge.search" }],
        });

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(async () =>
            await source.ProvideAsync(context, TestContext.Current.CancellationToken));

        Assert.Contains(nameof(IKnowledgeRetrievalPort), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUsesNameAgentCoreDoesNotShip_FailsTheBoot()
    {
        BuiltinToolSource source = new(new BuiltinToolPorts(new MapKnowledgePort(), null, null));
        var context = new ToolSourceContext(new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Tools = [new ToolConfiguration { Id = "x", Kind = ToolKind.Builtin, Uses = "knowledge.invent" }],
        });

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(async () =>
            await source.ProvideAsync(context, TestContext.Current.CancellationToken));

        Assert.Contains("knowledge.invent", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The schema keeps <c>model:</c> and <c>maxRounds:</c> to <c>kind: builtin</c>, and this is the
    /// half of the guard only the source can hold: whether a <c>uses:</c> name is a shipped agent or
    /// a plain function is known here. On a plain function either key boots clean and changes
    /// nothing, which decisions 4 and 8 reject.
    /// </summary>
    [Theory]
    [InlineData("model")]
    [InlineData("maxRounds")]
    public async Task AShippedAgentDialOnAPlainBuiltin_FailsTheBoot(string dial)
    {
        var declared = Search with
        {
            Model = dial == "model" ? new ModelReference { Ref = "cheap" } : null,
            MaxRounds = dial == "maxRounds" ? 4 : null,
        };

        BuiltinToolSource source = new(new BuiltinToolPorts(new MapKnowledgePort(), null, null));
        var context = new ToolSourceContext(new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Tools = [declared],
        });

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(async () =>
            await source.ProvideAsync(context, TestContext.Current.CancellationToken));

        Assert.Contains(Search.Id, failure.Message, StringComparison.Ordinal);
        Assert.Contains(dial, failure.Message, StringComparison.Ordinal);
        Assert.Contains(BuiltinToolNames.Draw, failure.Message, StringComparison.Ordinal);
    }
}

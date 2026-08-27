using AgentCore.Application.Configuration.Parsing;
using Xunit;

namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// Check 1 of section 8.5, and the JSON Pointer that section 8.7 requires of every load failure.
/// </summary>
public sealed class ConfigurationSchemaValidatorTests
{
    [Fact]
    public void TheExample_PassesCheckOne()
    {
        var document = ConfigurationLoader.ReadDocument(ExampleDocument.Yaml, ConfigurationFormat.Yaml);

        Assert.Empty(ConfigurationSchemaValidator.Evaluate(document));
    }

    [Fact]
    public void AnUnknownToolKind_FailsWithThePointerOfThatTool()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            tools:
              - { id: search_chunks, kind: builtin, uses: knowledge.search }
              - { id: lookup_order,  kind: ftp }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer == "/tools/1/kind");
        Assert.Contains("/tools/1/kind", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRequiredProperty_FailsWithThePointerOfItsParent()
    {
        const string document = """
            apiVersion: agentcore/v1
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Equal(ConfigurationError.RootPointer, failure.Pointer);
        Assert.Contains("name", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWrongApiVersion_FailsWithThePointerOfTheField()
    {
        const string document = """
            apiVersion: agentcore/v2
            name: broken
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains(failure.Errors, error => error.Pointer == "/apiVersion");
    }

    [Fact]
    public void ASecondExtractorTrigger_Fails()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            extractor:
              model: { ref: fill }
              when: in_reply
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains(failure.Errors, error => error.Pointer == "/extractor/when");
    }

    [Fact]
    public void AToolWriterWithNoPath_FailsWithThePointerOfTheSlot()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            state:
              orderStatus: { type: string, writer: tool }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains(failure.Errors, error => error.Pointer == "/state/orderStatus");
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void AFallbackReplyWithNoWords_FailsWithThePointerOfTheField(string written)
    {
        // Section 8.7 asks for a spoken fallback, and a line with no words is silence on a call.
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml($"apiVersion: agentcore/v1\nname: broken\nfallbackReply: {written}\n"));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer == "/fallbackReply");
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void ARefusalReplyWithNoWords_FailsWithThePointerOfTheField(string written)
    {
        // The refusal is spoken too, and a line with no words is silence on a call.
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml($"apiVersion: agentcore/v1\nname: broken\nrefusalReply: {written}\n"));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer == "/refusalReply");
    }

    [Fact]
    public void ARefusalReplyThatIsNotAString_FailsWithThePointerOfTheField()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            refusalReply: 7
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer == "/refusalReply");
        Assert.Contains("/refusalReply", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.1")]
    public void ASampleRateOutsideTheRange_FailsWithThePointerOfTheField(string written)
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml(
                $"apiVersion: agentcore/v1\nname: broken\nevaluation:\n  sampleRate: {written}\n"));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer == "/evaluation/sampleRate");
        Assert.Contains("/evaluation/sampleRate", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASampleRateThatIsNotANumber_FailsWithThePointerOfTheField()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            evaluation:
              sampleRate: "half"
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains(failure.Errors, error => error.Pointer == "/evaluation/sampleRate");
    }

    [Fact]
    public void AnUnknownEvaluationKey_Fails()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            evaluation:
              sampleRatio: 0.5
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains("sampleRatio", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownKnowledgeKey_Fails()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge: { vectors: zilliz }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer == "/providers/knowledge");
        Assert.Contains("vectors", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOldKnowledgeStoreKey_Fails()
    {
        // The store field went away. Each knowledge port now names its own adapter.
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge: { store: zilliz, root: ./kb }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains("store", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKnowledgeProviderWithNoFields_PassesCheckOne()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: plain
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge: {}
            """;

        var parsed = ConfigurationLoader.ReadDocument(document, ConfigurationFormat.Yaml);

        Assert.Empty(ConfigurationSchemaValidator.Evaluate(parsed));
    }

    [Fact]
    public void AnUnknownProperty_Fails()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            stage: greeting
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains("stage", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APolicyAndAGraphTogether_Fail()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            agents:
              items:
                - { id: greeter }
            policy:
              initial: greeting
              stages:
                - { id: greeting, agent: greeter, terminal: true }
            graph:
              pattern: sequential
              agents: [ greeter ]
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
    }

    [Fact]
    public void AGraphWithNodesAndEdges_Loads()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: reviewed
            agents:
              items:
                - { id: writer }
                - { id: reviewer }
            graph:
              nodes:
                - { id: draft,  agent: writer,   start: true }
                - { id: review, agent: reviewer, output: true }
              edges:
                - { from: draft, to: review, when: { var: resolved } }
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        Assert.NotNull(configuration.Graph);
        Assert.Null(configuration.Graph!.Pattern);
        Assert.Equal(2, configuration.Graph.Nodes.Count);
        Assert.True(configuration.Graph.Nodes[0].Start);
        Assert.True(configuration.Graph.Nodes[1].Output);

        var edge = Assert.Single(configuration.Graph.Edges);
        Assert.Equal("draft", edge.From);
        Assert.False(edge.When!.IsNamed);
        Assert.Equal("""{"var":"resolved"}""", edge.When!.Rule!.ToJsonString());
    }

    [Fact]
    public void AGraphWithAPattern_Loads()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: pipeline
            agents:
              items:
                - { id: writer }
                - { id: reviewer }
            graph:
              pattern: group_chat
              agents: [ writer, reviewer ]
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        Assert.Equal(AgentCore.Application.Configuration.Schema.GraphPattern.GroupChat, configuration.Graph!.Pattern);
        Assert.Equal(["writer", "reviewer"], configuration.Graph.Agents);
        Assert.Empty(configuration.Graph.Nodes);
    }

    [Fact]
    public void MalformedYaml_FailsBeforeCheckOne()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml("apiVersion: [agentcore/v1\nname: broken"));

        Assert.Equal(ConfigurationCheck.Syntax, failure.Check);
    }

    [Fact]
    public void ADuplicateKey_FailsBeforeCheckOne()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: first
            name: second
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Equal(ConfigurationCheck.Syntax, failure.Check);
    }

    // ---------------------------------------------------------------------------------------------
    // What the author of the document reads.
    //
    // The library writes for whoever wrote the schema. "All values fail against the false schema" is
    // exact and says nothing to somebody looking at a YAML file, and the branch machinery of `if` and
    // `oneOf` used to report its own misses as if they were the document's. These pin the sentences
    // check 1 hands back instead.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnUnknownKey_SaysTheSchemaDoesNotKnowIt()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml("apiVersion: agentcore/v1\nname: broken\nsampleRatio: 0.5\n"));

        Assert.DoesNotContain("false schema", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(failure.Errors, error => error.Message.Contains("sampleRatio", StringComparison.Ordinal));
    }

    [Fact]
    public void AWriterWithNoPath_DoesNotAdviseAnotherWriter()
    {
        // The slot declares `writer: tool` and leaves out `from`. The schema reaches that by asking
        // each `if` in turn, and the misses used to be reported: the author was told the writer
        // should have been `counter`, and then `const`, which is the opposite of the fix.
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            state:
              orderStatus: { type: string, writer: tool }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/state/orderStatus", error.Pointer);
        Assert.Contains("from", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("counter", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("const", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownEnumValue_ListsTheValuesTheKeyAccepts()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            tools:
              - { id: lookup_order, kind: ftp }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/tools/0/kind", error.Pointer);
        Assert.Contains("builtin, http, binding, agent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APolicyAndAGraphTogether_SayWhatTheDocumentDid()
    {
        // The rule has no keyword of its own — it is a `not` over a dependent schema — so the schema
        // writes it out in prose and check 1 reads that back.
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            agents:
              items:
                - { id: greeter }
            policy:
              initial: greeting
              stages:
                - { id: greeting, agent: greeter, terminal: true }
            graph:
              pattern: sequential
              agents: [ greeter ]
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        var error = Assert.Single(failure.Errors);
        Assert.Contains("policy and graph", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AGraphThatIsBothShapes_NamesTheShapesItCouldHaveBeen()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            graph:
              pattern: sequential
              agents: [ writer ]
              nodes: [ { id: draft } ]
              edges: []
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains(
            failure.Errors,
            error => error.Pointer == "/graph"
                     && error.Message.Contains("a pattern graph", StringComparison.Ordinal)
                     && error.Message.Contains("nodes and edges", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIdentifierThatIsNotOne_ShowsTheFormTheKeyTakes()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            tools:
              - { id: "9lives", kind: builtin, uses: knowledge.search }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/tools/0/id", error.Pointer);
        Assert.Contains("^[A-Za-z_][A-Za-z0-9_-]*$", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmbeddedSchema_IsPresent()
        => Assert.Contains("agentcore/v1", ConfigurationSchemaValidator.SchemaJson, StringComparison.Ordinal);

    [Fact]
    public void ABuiltinToolWithParameters_FailsWithThePointerOfTheTool()
    {
        // The C# still reads fixed argument names for a builtin (BuiltinToolSource). A document
        // that overrides the schema makes the model fill boxes the C# never reads.
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            tools:
              - { id: search_chunks, kind: builtin, uses: knowledge.search, parameters: { type: object } }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer == "/tools/0/parameters");
    }

    [Fact]
    public void AToolWithMaxRoundsBelowOne_FailsWithThePointerOfTheTool()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            tools:
              - { id: draw, kind: builtin, uses: ui.draw, maxRounds: 0 }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer.Contains("maxRounds", StringComparison.Ordinal));
    }

    /// <summary>
    /// Only the <c>kind: builtin</c> path reads <c>model:</c> and <c>maxRounds:</c>. On any other
    /// kind they would be accepted and then never looked at, which is the silent failure decisions 4
    /// and 8 of the tool registry design reject.
    /// </summary>
    [Theory]
    [InlineData("model: { ref: cheap }", "model")]
    [InlineData("maxRounds: 4", "maxRounds")]
    public void AShippedAgentDialOnAKindThatNeverReadsIt_FailsWithThePointerOfTheKey(string key, string name)
    {
        var document = $$"""
            apiVersion: agentcore/v1
            name: broken
            tools:
              - { id: lookup, kind: http, request: { method: GET, url: "https://example.test" }, {{key}} }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer.Contains(name, StringComparison.Ordinal));
    }

    [Fact]
    public void ABuiltinToolWithNoParameters_PassesCheckOne()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: fine
            tools:
              - { id: search_chunks, kind: builtin, uses: knowledge.search }
            """;

        var parsed = ConfigurationLoader.ReadDocument(document, ConfigurationFormat.Yaml);

        Assert.Empty(ConfigurationSchemaValidator.Evaluate(parsed));
    }

    [Theory]
    [InlineData("http",    "request: { method: GET, url: \"https://example.test\" }")]
    [InlineData("binding", "binds: host.lookup")]
    [InlineData("agent",   "agent: reviewer")]
    public void ANonBuiltinToolWithParameters_PassesCheckOne(string kind, string discriminatorField)
    {
        var document = "apiVersion: agentcore/v1\n"
            + "name: fine\n"
            + "tools:\n"
            + "  - id: delegated\n"
            + "    kind: " + kind + "\n"
            + "    " + discriminatorField + "\n"
            + "    parameters: { type: object }\n";

        var parsed = ConfigurationLoader.ReadDocument(document, ConfigurationFormat.Yaml);

        Assert.Empty(ConfigurationSchemaValidator.Evaluate(parsed));
    }
}

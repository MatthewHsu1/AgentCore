using System.Text;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Validation;

/// <summary>
/// Checks 2 to 8 of section 8.5, and rule 14 of section 11.
/// </summary>
/// <remarks>
/// Rule 14: each of the eight checks has a failing-configuration test that asserts the message and
/// the JSON Pointer, and check 5 prints the state that makes two guards overlap. Check 1 is tested
/// in <see cref="ConfigurationSchemaValidatorTests"/>, and the seven others are tested here.
/// </remarks>
public sealed class ConfigurationValidatorTests
{
    /// <summary>The smallest provider set that satisfies checks 1 and 2 on its own.</summary>
    private const string MinimalProviders =
        """
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    /// <summary><see cref="MinimalProviders"/> without <c>llm:</c>, for checks that never read a model.</summary>
    private const string SpeechOnlyProviders =
        """
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
        """;

    [Fact]
    public void TheExample_PassesEveryCheck()
    {
        var configuration = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);

        var result = ConfigurationValidator.Evaluate(configuration);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void TheShippedExampleFile_PassesEveryCheck()
    {
        var path = Path.Combine(RepositoryRoot(), "config", "example.yaml");
        Assert.True(File.Exists(path), $"The shipped example is missing at '{path}'.");

        var result = ConfigurationValidator.Evaluate(ConfigurationLoader.LoadFile(path));

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void TheExampleAsJson_PassesEveryCheck()
    {
        var configuration = ConfigurationLoader.LoadJson(ExampleDocument.Json);

        Assert.Empty(ConfigurationValidator.Evaluate(configuration).Errors);
    }

    // ---------------------------------------------------------------------------------------------
    // Check 2: reference resolution.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void AnUnknownAgent_FailsCheckTwoWithThePointerOfTheStage()
    {
        const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: greeter }
            entries:
              main:
                policy:
                  initial: greeting
                  stages:
                    - { id: greeting, agent: ghost, terminal: true }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/entries/main/policy/stages/0/agent", error.Pointer);
        Assert.Equal("the agent 'ghost' is not declared in agents.items", error.Message);
    }

    [Fact]
    public void AnUnknownTool_FailsCheckTwoWithThePointerOfTheSlot()
      {
        const string document = """
            apiVersion: agentcore/v1
            state:
              orderStatus: { type: string, writer: tool, from: lookup_order.status }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/state/orderStatus/from", error.Pointer);
        Assert.Equal("nothing serves the tool 'lookup_order'. Declare it in tools:, or check that an mcp: server offers it.", error.Message);
    }

    [Fact]
    public void ABackgroundChildThatIsNotAnAgent_FailsCheckTwoWithThePointerOfThatChild()
    {
        const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - id: coder
                  background: [searcher]
            entries:
              main:
                agent: coder
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/agents/items/0/background/0", error.Pointer);
        Assert.Equal("the agent 'searcher' is not declared in agents.items", error.Message);
    }

    [Fact]
    public void AnUnknownGuard_FailsCheckTwoWithThePointerOfTheExit()
    {
        const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: greeter }
            entries:
              main:
                policy:
                  initial: greeting
                  stages:
                    - id: greeting
                      agent: greeter
                      to: [ { stage: close, when: ghostGuard } ]
                    - { id: close, agent: greeter, terminal: true }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/entries/main/policy/stages/0/to/0/when", error.Pointer);
        Assert.Equal("the guard 'ghostGuard' is not declared in guards:", error.Message);
      }

    [Fact]
    public void AnUnknownExtractorModel_FailsCheckTwoWithThePointerOfTheReference()
      {
        const string document = $$"""
            apiVersion: agentcore/v1
            extractor:
              model: { ref: ghost }
            {{MinimalProviders}}
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/extractor/model/ref", error.Pointer);
        Assert.Equal("the model 'ghost' is not declared in providers.llm", error.Message);
    }

    [Fact]
    public void AnUnknownJudgeModel_FailsCheckTwoWithThePointerOfTheReference()
    {
        const string document = $$"""
            apiVersion: agentcore/v1
            evaluation:
              judge: { ref: ghost }
            {{MinimalProviders}}
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/evaluation/judge/ref", error.Pointer);
        Assert.Equal("the model 'ghost' is not declared in providers.llm", error.Message);
    }

    [Fact]
    public void AnUnknownTitlerModel_FailsCheckTwoWithThePointerOfTheReference()
    {
        const string document = $$"""
            apiVersion: agentcore/v1
            titler:
              model: { ref: ghost }
            {{MinimalProviders}}
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/titler/model/ref", error.Pointer);
        Assert.Equal("the model 'ghost' is not declared in providers.llm", error.Message);
    }

    [Fact]
    public void AnEvaluationSectionWithNoJudge_PassesCheckTwo()
    {
        const string document = $$"""
            apiVersion: agentcore/v1
            evaluation:
              sampleRate: 0
            {{MinimalProviders}}
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        Assert.Empty(Evaluate(document, ConfigurationCheck.ReferenceResolution));
    }

    [Fact]
    public void AnUnknownAgentModel_FailsCheckTwoWithThePointerOfThatAgent()
    {
        const string document = $$"""
            apiVersion: agentcore/v1
            {{MinimalProviders}}
            agents:
              items:
                - { id: greeter, model: { ref: ghost } }
            entries:
              main:
                agent: greeter
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/agents/items/0/model/ref", error.Pointer);
        Assert.Equal("the model 'ghost' is not declared in providers.llm", error.Message);
    }

    [Fact]
    public void AnUnknownDefaultModel_FailsCheckTwoWithThePointerOfTheDefaults()
    {
        const string document = $$"""
            apiVersion: agentcore/v1
            agents:
              defaults:
                model: { ref: ghost }
              items:
                - { id: greeter }
            {{MinimalProviders}}
            entries:
              main:
                agent: greeter
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/agents/defaults/model/ref", error.Pointer);
        Assert.Equal("the model 'ghost' is not declared in providers.llm", error.Message);
    }

    [Fact]
    public void AModelReferenceWithNoProvidersSection_FailsCheckTwo()
    {
        // An absent providers: declares no model name, so the reference is unknown. Check 2 reads an
        // absent tools: and an absent agents: the same way.
        const string document = """
            apiVersion: agentcore/v1
            extractor:
              model: { ref: fill }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/extractor/model/ref", error.Pointer);
        Assert.Equal("the model 'fill' is not declared in providers.llm", error.Message);
    }

    [Fact]
    public void ADocumentThatNamesNoModel_PassesCheckTwoWithNoProvidersSection()
    {
        const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: greeter }
            entries:
              main:
                agent: greeter
            """;

        Assert.Empty(ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(document)).Errors);
    }

    [Fact]
    public void AnUnusedAgentToolNamingAnUndeclaredAgent_FailsCheckTwo()
    {
        // No agent lists this tool, so only the compiler used to catch it, and only later.
        const string document = """
            apiVersion: agentcore/v1
            tools:
              - { id: call_ghost, kind: agent, agent: ghost }
            agents:
              items:
                - { id: planner }
            entries:
              main:
                agent: planner
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/tools/0/agent", error.Pointer);
        Assert.Equal("the agent 'ghost' is not declared in agents.items", error.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // Check 2, mcp: ids: two servers must not share one id.
    // ---------------------------------------------------------------------------------------------
    /// <summary>
    /// Two <c>mcp:</c> entries with disjoint <c>allow:</c> sets would otherwise boot silently — nothing
    /// in the schema, this validator, or <c>McpToolSource</c> catches it until an overlapping pair
    /// happens to collide on a served tool id, and even then the failure names the tool, never the
    /// duplicated server id. This runs before any connection opens.
    /// </summary>
    [Fact]
    public void TwoMcpEntriesSharingAnId_FailCheckTwoNamingTheId()
    {
        const string document = """
            apiVersion: agentcore/v1
            mcp:
              - id: jira
                transport: stdio
                command: [npx]
                allow: [create_issue]
              - id: jira
                transport: stdio
                command: [npx]
                allow: [search_issues]
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.ReferenceResolution));

        Assert.Equal("/mcp/1/id", error.Pointer);
        Assert.Contains("jira", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Check 2, knowledge scope: a state-built scope must not produce a filter nobody meant.
    // ---------------------------------------------------------------------------------------------
    // KnowledgeStartup skips the store factory for a host-supplied port, and returns null when
    // KnowledgeStores is unset, but CallSession composes the scope regardless — so these are document
    // checks, not store checks.
    private static AgentCoreConfiguration Scoped(
        IReadOnlyList<string> fromState,
        KnowledgeWildcardConfiguration? wildcard,
        IDictionary<string, StateSlotConfiguration> state,
        ExtractorConfiguration? extractor = null,
        KnowledgeAmbiguityConfiguration? ambiguity = null,
        string? mapper = null) => new()
    {
        ApiVersion = "agentcore/v1",
        Agents = new AgentsConfiguration { Items = [new AgentConfiguration { Id = "planner" }] },
        Entries = new Dictionary<string, EntryConfiguration> { ["main"] = new EntryConfiguration { Agent = "planner" } },
        State = new Dictionary<string, StateSlotConfiguration>(state, StringComparer.Ordinal),
        Extractor = extractor,
        Providers = new()
        {
            // AnExtractor() references the model 'small'. Declaring it here means a caller that
            // passes an extractor never also, silently, trips check 2's reference-resolution row —
            // which every predicate-filtered assertion in this file would otherwise hide.
            Llm = [new() { Kind = "openai", Model = "gpt", As = "small" }],
            Knowledge = new()
            {
                Kind = "qdrant",
                Collection = "kb",
                Fields = new() { Body = "text" },
                Mapper = mapper,
                Scope = new()
                {
                    Template = "facets.{key}",
                    Wildcard = wildcard,
                    FromState = fromState,
                },
                Ambiguity = ambiguity,
            },
        },
    };

    private static StateSlotConfiguration FacetSlot() => new()
    {
        Type = StateSlotType.String,
        Writer = StateWriter.Extractor,
        EnumValues = [JsonValue.Create("f63")!],
    };

    private static KnowledgeAmbiguityConfiguration AnAmbiguity() => new();

    private static readonly KnowledgeWildcardConfiguration Star =
        new() { Value = "*", Facets = ["applies_to"] };

    private static ExtractorConfiguration AnExtractor() =>
        new() { Model = new ModelReference { Ref = "small" } };

    /// <summary>One facet slot, named by <see cref="Star"/> and by <c>fromState</c>.</summary>
    private static AgentCoreConfiguration OneFacetScope(
        KnowledgeAmbiguityConfiguration? ambiguity = null,
        string? mapper = null) =>
        Scoped(
            ["applies_to"],
            Star,
            new Dictionary<string, StateSlotConfiguration> { ["applies_to"] = FacetSlot() },
            AnExtractor(),
            ambiguity,
            mapper);

    /// <summary>
    /// Two facet slots. This is the smallest scope that keeps the single-facet ambiguity warning
    /// (K33) silent, so it is the base for every test that asserts no warning.
    /// </summary>
    private static AgentCoreConfiguration TwoFacetScope(
        KnowledgeAmbiguityConfiguration? ambiguity = null,
        string? mapper = null) =>
        Scoped(
            ["applies_to", "brand"],
            new KnowledgeWildcardConfiguration { Value = "*", Facets = ["applies_to", "brand"] },
            new Dictionary<string, StateSlotConfiguration>
            {
                ["applies_to"] = FacetSlot(),
                ["brand"] = FacetSlot(),
            },
            AnExtractor(),
            ambiguity,
            mapper);

    [Fact]
    public void Evaluate_FromStateNamesAnUndeclaredSlot_Fails()
    {
        var result = ConfigurationValidator.EvaluateStructure(
            Scoped(["applies_to"], Star, new Dictionary<string, StateSlotConfiguration>(), AnExtractor()));

        var error = Assert.Single(result.Errors, e => e.Pointer == "/providers/knowledge/scope/fromState");
        Assert.Contains("applies_to", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_FacetSlotDeclaresADefault_Fails()
    {
        var slot = FacetSlot() with { Default = JsonValue.Create("f63") };

        var result = ConfigurationValidator.EvaluateStructure(
            Scoped(
                ["applies_to"],
                Star,
                new Dictionary<string, StateSlotConfiguration> { ["applies_to"] = slot },
                AnExtractor()));

        Assert.Single(result.Errors, e => e.Pointer == "/state/applies_to/default");
    }

    [Fact]
    public void Evaluate_FromStateWithoutAWildcard_Fails()
    {
        var result = ConfigurationValidator.EvaluateStructure(
            Scoped(
                ["applies_to"],
                wildcard: null,
                new Dictionary<string, StateSlotConfiguration> { ["applies_to"] = FacetSlot() },
                AnExtractor()));

        Assert.Single(result.Errors, e => e.Pointer == "/providers/knowledge/scope/wildcard");
    }

    [Fact]
    public void Evaluate_FacetSlotIsNotTypeString_Fails()
    {
        var slot = FacetSlot() with { Type = StateSlotType.Boolean };

        var result = ConfigurationValidator.EvaluateStructure(
            Scoped(
                ["applies_to"],
                Star,
                new Dictionary<string, StateSlotConfiguration> { ["applies_to"] = slot },
                AnExtractor()));

        var error = Assert.Single(result.Errors, e => e.Pointer == "/state/applies_to/type");
        Assert.Contains("applies_to", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_FacetSlotIsNotWrittenByExtractor_Fails()
    {
        var slot = FacetSlot() with { Writer = StateWriter.Const, Value = JsonValue.Create("f63") };

        var result = ConfigurationValidator.EvaluateStructure(
            Scoped(
                ["applies_to"],
                Star,
                new Dictionary<string, StateSlotConfiguration> { ["applies_to"] = slot },
                AnExtractor()));

        var error = Assert.Single(result.Errors, e => e.Pointer == "/state/applies_to/writer");
        Assert.Contains("applies_to", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_FacetSlotDeclaresNoEnum_Fails()
    {
        var slot = FacetSlot() with { EnumValues = null };

        var result = ConfigurationValidator.EvaluateStructure(
            Scoped(
                ["applies_to"],
                Star,
                new Dictionary<string, StateSlotConfiguration> { ["applies_to"] = slot },
                AnExtractor()));

        var error = Assert.Single(result.Errors, e => e.Pointer == "/state/applies_to/enum");
        Assert.Contains("applies_to", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_FromStateWithNoExtractor_Fails()
    {
        var result = ConfigurationValidator.EvaluateStructure(
            Scoped(
                ["applies_to"],
                Star,
                new Dictionary<string, StateSlotConfiguration> { ["applies_to"] = FacetSlot() }));

        Assert.Single(result.Errors, e => e.Pointer == "/extractor");
    }

    [Fact]
    public void Evaluate_FromStateNameNotInWildcardFacets_Fails()
    {
        // wildcard.facets omits 'applies_to' but every member it does name ('brand') is still in
        // fromState, so only the fromState -> facets direction trips here; the reverse (facets ->
        // fromState) has its own test below.
        var wildcard = new KnowledgeWildcardConfiguration { Value = "*", Facets = ["brand"] };

        var result = ConfigurationValidator.EvaluateStructure(
            Scoped(
                ["applies_to", "brand"],
                wildcard,
                new Dictionary<string, StateSlotConfiguration>
                {
                    ["applies_to"] = FacetSlot(),
                    ["brand"] = FacetSlot(),
                },
                AnExtractor()));

        var error = Assert.Single(result.Errors, e => e.Pointer == "/providers/knowledge/scope/wildcard/facets");
        Assert.Contains("applies_to", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WildcardFacetNotInFromState_Fails()
    {
        var wildcard = new KnowledgeWildcardConfiguration { Value = "*", Facets = ["applies_to", "other_facet"] };

        var result = ConfigurationValidator.EvaluateStructure(
            Scoped(
                ["applies_to"],
                wildcard,
                new Dictionary<string, StateSlotConfiguration> { ["applies_to"] = FacetSlot() },
                AnExtractor()));

        var error = Assert.Single(result.Errors);
        Assert.Equal("/providers/knowledge/scope/wildcard/facets", error.Pointer);
        Assert.Contains("other_facet", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_FacetWildcardValueIsWhitespace_Fails()
    {
        var wildcard = new KnowledgeWildcardConfiguration { Value = "   ", Facets = ["applies_to"] };

        var result = ConfigurationValidator.EvaluateStructure(
            Scoped([], wildcard, new Dictionary<string, StateSlotConfiguration>()));

        Assert.Single(result.Errors, e => e.Pointer == "/providers/knowledge/scope/wildcard/value");
    }

    [Fact]
    public void Evaluate_WildcardWithoutFromState_Passes()
    {
        // A deployment may resolve its own facets and open them as the host ambient, wanting only the
        // wildcard's widening. StateKnowledgeScope.Compose refuses the reverse, never this.
        var result = ConfigurationValidator.EvaluateStructure(
            Scoped([], Star, new Dictionary<string, StateSlotConfiguration>()));

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Evaluate_FacetWildcardDeclaresNoFacets_Fails()
    {
        var wildcard = new KnowledgeWildcardConfiguration { Value = "*", Facets = [] };

        var result = ConfigurationValidator.EvaluateStructure(
            Scoped([], wildcard, new Dictionary<string, StateSlotConfiguration>()));

        Assert.Single(result.Errors, e => e.Pointer == "/providers/knowledge/scope/wildcard/facets");
    }

    // ---------------------------------------------------------------------------------------------
    // Check 2, ambiguity: section 10 of the ambiguity design.
    //
    // Every fixture below keeps fromState and wildcard.facets in lockstep (both naming exactly the
    // facets the test declares slots for) so the row-8 and enum checks stay silent, and sets
    // ambiguity: and wildcard: wherever their absence would otherwise trip rows 5 or 6.
    // Each refusal test asserts the *total* error count, not just a filtered pointer match, so a
    // fixture that quietly grows a second error cannot hide behind the assertion.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void Evaluate_AmbiguityWithNoWildcard_Fails()
    {
        var result = ConfigurationValidator.EvaluateStructure(
            Scoped([], wildcard: null, new Dictionary<string, StateSlotConfiguration>(), ambiguity: AnAmbiguity()));

        var error = Assert.Single(result.Errors);
        Assert.Equal("/providers/knowledge/ambiguity", error.Pointer);
    }

    [Fact]
    public void Evaluate_AmbiguityMaxCandidatesBelowTwo_Fails()
    {
        var ambiguity = new KnowledgeAmbiguityConfiguration { MaxCandidates = 1 };

        var result = ConfigurationValidator.EvaluateStructure(OneFacetScope(ambiguity));

        var error = Assert.Single(result.Errors);
        Assert.Equal("/providers/knowledge/ambiguity/maxCandidates", error.Pointer);
    }

    [Fact]
    public void Evaluate_AmbiguityMaxAsksBelowZero_Fails()
    {
        var ambiguity = new KnowledgeAmbiguityConfiguration { MaxAsks = -1 };

        var result = ConfigurationValidator.EvaluateStructure(OneFacetScope(ambiguity));

        var error = Assert.Single(result.Errors);
        Assert.Equal("/providers/knowledge/ambiguity/maxAsks", error.Pointer);
    }

    [Fact]
    public void Evaluate_AmbiguityMaxAsksOfZero_IsLegal()
    {
        // K38: 0 means gate only. It must never trip the same floor that refuses a negative count.
        var ambiguity = new KnowledgeAmbiguityConfiguration { MaxAsks = 0 };

        var result = ConfigurationValidator.EvaluateStructure(OneFacetScope(ambiguity));

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Evaluate_AmbiguityProbeDeadlineSecondsBelowOne_Fails()
    {
        var ambiguity = new KnowledgeAmbiguityConfiguration { ProbeDeadlineSeconds = 0 };

        var result = ConfigurationValidator.EvaluateStructure(OneFacetScope(ambiguity));

        var error = Assert.Single(result.Errors);
        Assert.Equal("/providers/knowledge/ambiguity/probeDeadlineSeconds", error.Pointer);
    }

    [Fact]
    public void Evaluate_AmbiguityProbeWaitMarginSecondsBelowOne_Fails()
    {
        var ambiguity = new KnowledgeAmbiguityConfiguration { ProbeWaitMarginSeconds = 0 };

        var result = ConfigurationValidator.EvaluateStructure(OneFacetScope(ambiguity));

        var error = Assert.Single(result.Errors);
        Assert.Equal("/providers/knowledge/ambiguity/probeWaitMarginSeconds", error.Pointer);
    }

    [Fact]
    public void Evaluate_AmbiguityWithCustomMapper_Fails()
    {
        // The probe reads Extras, which only the built-in field mapper fills.
        var result = ConfigurationValidator.EvaluateStructure(
            TwoFacetScope(AnAmbiguity(), mapper: "custom-mapper"));

        var error = Assert.Single(result.Errors);
        Assert.Equal("/providers/knowledge/mapper", error.Pointer);
    }

    [Fact]
    public void Evaluate_AmbiguityProbeDeadlineSecondsAboveTheTimerCeiling_Fails()
    {
        // CancelAfter throws above int.MaxValue milliseconds, from a call site with no pointer.
        var ambiguity = new KnowledgeAmbiguityConfiguration { ProbeDeadlineSeconds = 3_000_000 };

        var result = ConfigurationValidator.EvaluateStructure(OneFacetScope(ambiguity));

        var error = Assert.Single(result.Errors);
        Assert.Equal("/providers/knowledge/ambiguity/probeDeadlineSeconds", error.Pointer);
        Assert.Equal(ConfigurationCheck.ValueRange, error.Check);
    }

    [Fact]
    public void Evaluate_AmbiguityProbeWaitMarginSecondsAboveTheTimerCeiling_Fails()
    {
        var ambiguity = new KnowledgeAmbiguityConfiguration { ProbeWaitMarginSeconds = 3_000_000 };

        var result = ConfigurationValidator.EvaluateStructure(OneFacetScope(ambiguity));

        var error = Assert.Single(result.Errors);
        Assert.Equal("/providers/knowledge/ambiguity/probeWaitMarginSeconds", error.Pointer);
    }

    [Fact]
    public void Evaluate_AmbiguityWithEmptyFromState_Warns()
    {
        // No facet to drop is as unreachable as one, and the wildcard-only shape now loads.
        var result = ConfigurationValidator.EvaluateStructure(
            Scoped([], Star, new Dictionary<string, StateSlotConfiguration>(), ambiguity: AnAmbiguity()));

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("/providers/knowledge/ambiguity", warning.Pointer);
    }

    [Fact]
    public void Evaluate_AmbiguityWithSingleFacetFromState_Warns()
    {
        var result = ConfigurationValidator.EvaluateStructure(OneFacetScope(AnAmbiguity()));

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("/providers/knowledge/ambiguity", warning.Pointer);
    }

    [Fact]
    public void Evaluate_AmbiguityWithTwoFacetsFromState_DoesNotWarn()
    {
        var result = ConfigurationValidator.EvaluateStructure(TwoFacetScope(AnAmbiguity()));

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Evaluate_NoAmbiguity_BindsNullAndAddsNothing()
    {
        var configuration = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);

        Assert.Null(configuration.Providers?.Knowledge?.Ambiguity);

        var result = ConfigurationValidator.Evaluate(configuration);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    // ---------------------------------------------------------------------------------------------
    // Check 3: one writer for each slot.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void AReservedSlotDeclaredInState_FailsCheckThreeWithThePointerOfTheSlot()
    {
        const string document = """
            apiVersion: agentcore/v1
            state:
              stage: { type: string, writer: extractor }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.SlotWriters));

        Assert.Equal("/state/stage", error.Pointer);
        Assert.Equal(
            "the slot has two writers: 'stage' is a reserved read-only slot that is always present, and state: declares it again",
            error.Message);
    }

    [Fact]
    public void ASlotWithASecondWriterField_FailsCheckThreeWithThePointerOfThatField()
    {
        const string document = """
            apiVersion: agentcore/v1
            state:
              failedResolveTurns:
                type: integer
                writer: counter
                increment: { var: turnIndex }
                value: 0
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.SlotWriters));

        Assert.Equal("/state/failedResolveTurns/value", error.Pointer);
        Assert.Equal("the slot has two writers: writer: counter owns it, and 'value:' names a second", error.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // Check 4: guard operators and variables.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void LooseEquality_FailsCheckFourAndTheMessageNamesTheReplacement()
    {
        const string document = """
            apiVersion: agentcore/v1
            state:
              resolved: { type: boolean, default: false, writer: extractor }
            guards:
              fixedNow: { "==": [ { var: resolved }, true ] }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardOperators));

        Assert.Equal("/guards/fixedNow", error.Pointer);
        Assert.Equal("the operator '==' is rejected because it is loose equality. Use '===' instead.", error.Message);
    }

    [Theory]
    [InlineData("==")]
    [InlineData("!=")]
    [InlineData("log")]
    [InlineData("map")]
    [InlineData("filter")]
    [InlineData("reduce")]
    [InlineData("all")]
    [InlineData("some")]
    [InlineData("none")]
    [InlineData("merge")]
    [InlineData("cat")]
    [InlineData("substr")]
    public void EveryRejectedOperator_FailsCheckFour(string rejected)
    {
        var document = $$"""
            apiVersion: agentcore/v1
            state:
              resolved: { type: boolean, default: false, writer: extractor }
            guards:
              bad: { "{{rejected}}": [ { var: resolved }, 1 ] }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardOperators));

        Assert.Equal("/guards/bad", error.Pointer);
        Assert.Equal(GuardOperators.DescribeRejection(rejected), error.Message);
    }

    [Fact]
    public void AVarNamingAnUndeclaredSlot_FailsCheckFour()
    {
        const string document = """
            apiVersion: agentcore/v1
            guards:
              ghost: { var: neverDeclared }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardOperators));

        Assert.Equal("/guards/ghost", error.Pointer);
        Assert.Equal("the rule reads the slot 'neverDeclared', and state: does not declare it", error.Message);
    }

    [Fact]
    public void ANumericComparisonAgainstABooleanSlot_FailsCheckFour()
    {
        const string document = """
            apiVersion: agentcore/v1
            state:
              resolved: { type: boolean, default: false, writer: extractor }
            guards:
              tooMany: { ">=": [ { var: resolved }, 3 ] }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardOperators));

        Assert.Equal("/guards/tooMany", error.Pointer);
        Assert.Equal(
            "the operator '>=' compares the slot 'resolved', and that slot is a boolean rather than a number",
            error.Message);
    }

    [Fact]
    public void TheUnarySugarFormOfDoubleNegation_FailsCheckFourAndTheMessageNamesTheArrayForm()
    {
        const string document = """
            apiVersion: agentcore/v1
            state:
              resolved: { type: boolean, default: false, writer: extractor }
            guards:
              isResolved: { "!!": { var: resolved } }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardOperators));

        Assert.Equal("/guards/isResolved", error.Pointer);
        Assert.Equal(GuardOperators.DoubleNegationSugarRejection, error.Message);
        Assert.Contains("""{"!!": [ x ]}""", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheArrayFormOfDoubleNegation_PassesCheckFour()
    {
        const string document = """
            apiVersion: agentcore/v1
            state:
              resolved: { type: boolean, default: false, writer: extractor }
            guards:
              isResolved: { "!!": [ { var: resolved } ] }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        Assert.Empty(ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(document)).Errors);
    }

    [Fact]
    public void TheUnarySugarFormOfSingleNegation_PassesCheckFour()
    {
        // JsonLogic reads the sugar for '!' and not for '!!', so only '!!' is rejected.
        const string document = """
            apiVersion: agentcore/v1
            state:
              resolved: { type: boolean, default: false, writer: extractor }
            guards:
              notResolved: { "!": { var: resolved } }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        Assert.Empty(ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(document)).Errors);
    }

    [Fact]
    public void AReservedSlotReadInARule_PassesCheckFour()
    {
        const string document = """
            apiVersion: agentcore/v1
            guards:
              inResolve: { "===": [ { var: stage }, "resolve" ] }
              longCall:  { ">=": [ { var: callDurationSeconds }, 90 ] }
              lateTurn:  { ">":  [ { var: turnIndex }, 4 ] }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

        Assert.Empty(ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(document)).Errors);
    }

    // ---------------------------------------------------------------------------------------------
    // Check 5: exclusivity and coverage by evaluation.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void TwoSiblingGuardsTrueAtOnce_FailCheckFiveAndTheMessagePrintsTheState()
    {
        const string document = """
            apiVersion: agentcore/v1
            state:
              callerAskedForHuman: { type: boolean, default: false, writer: extractor }
              callerSaidGoodbye:   { type: boolean, default: false, writer: extractor }
            guards:
              saidGoodbye: { var: callerSaidGoodbye }
              wantsHuman:  { var: callerAskedForHuman }
            agents:
              items:
                - { id: greeter }
            entries:
              main:
                policy:
                  initial: identify
                  stages:
                    - id: identify
                      agent: greeter
                      to:
                        - { stage: close,    when: saidGoodbye }
                        - { stage: escalate, when: wantsHuman }
                    - { id: close,    agent: greeter, terminal: true }
                    - { id: escalate, agent: greeter, terminal: true }

            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardExclusivity));

        Assert.Equal("/entries/main/policy/stages/0/to/1/when", error.Pointer);
        Assert.Equal(
              "the guard 'saidGoodbye' and the guard 'wantsHuman' are both true at the same time. "
              + """The state that triggers it is {"callerSaidGoodbye":true,"callerAskedForHuman":true}.""",
              error.Message);
      }

    [Fact]
    public void AnUnconditionalExitBesideAGuardedOne_FailsCheckFive()
      {
        const string document = """
              apiVersion: agentcore/v1
              state:
                resolved: { type: boolean, default: false, writer: extractor }
              guards:
                fixedNow: { var: resolved }
              agents:
                items:
                  - { id: greeter }
              entries:
                main:
                  policy:
                    initial: resolve
                    stages:
                      - id: resolve
                        agent: greeter
                        to:
                          - { stage: close, when: fixedNow }
                          - { stage: escalate }
                      - { id: close,    agent: greeter, terminal: true }
                      - { id: escalate, agent: greeter, terminal: true }
              """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardExclusivity));

        Assert.Equal("/entries/main/policy/stages/0/to/1", error.Pointer);
        Assert.Contains("the unconditional exit to 'escalate'", error.Message, StringComparison.Ordinal);
        Assert.Contains("""{"resolved":true}""", error.Message, StringComparison.Ordinal);
      }

    [Fact]
    public void TwoGraphEdgesTrueAtOnce_FailCheckFiveOnTheEdge()
      {
        const string document = """
            apiVersion: agentcore/v1
            state:
              resolved:  { type: boolean, default: false, writer: extractor }
              escalated: { type: boolean, default: false, writer: extractor }
            guards:
              fixedNow: { var: resolved }
              handedOff: { or: [ { var: escalated }, { var: resolved } ] }
            agents:
              items:
                - { id: worker }
                - { id: closer }
            entries:
              main:
                graph:
                  nodes:
                    - { id: start, agent: worker, start: true }
                    - { id: left,  agent: closer, output: true }
                    - { id: right, agent: closer, output: true }
                  edges:
                    - { from: start, to: left,  when: fixedNow }
                    - { from: start, to: right, when: handedOff }

            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardExclusivity));

        Assert.Equal("/entries/main/graph/edges/1/when", error.Pointer);
        Assert.Contains("the guard 'fixedNow' and the guard 'handedOff'", error.Message, StringComparison.Ordinal);
        Assert.Contains("""{"resolved":true,"escalated":false}""", error.Message, StringComparison.Ordinal);
      }

    [Fact]
    public void ANumberSlot_IsBucketedAroundEveryThresholdTheSiblingsMention()
      {
          // failedResolveTurns >= 3 and failedResolveTurns < 3 never overlap, so the buckets must not
          // invent a false positive. failedResolveTurns >= 3 and failedResolveTurns > 1 do overlap at 4.
        const string document = """
              apiVersion: agentcore/v1
              state:
                failedResolveTurns: { type: integer, default: 0, writer: counter, increment: { var: turnIndex } }
              guards:
                exhausted: { ">=": [ { var: failedResolveTurns }, 3 ] }
                tried:     { ">":  [ { var: failedResolveTurns }, 1 ] }
              agents:
                items:
                  - { id: greeter }
              entries:
                main:
                  policy:
                    initial: resolve
                    stages:
                      - id: resolve
                        agent: greeter
                        to:
                          - { stage: close,    when: exhausted }
                          - { stage: escalate, when: tried }
                      - { id: close,    agent: greeter, terminal: true }
                      - { id: escalate, agent: greeter, terminal: true }
              """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GuardExclusivity));

        Assert.Equal("/entries/main/policy/stages/0/to/1/when", error.Pointer);
        Assert.Contains("""{"failedResolveTurns":3}""", error.Message, StringComparison.Ordinal);
      }

    [Fact]
    public void AStateDomainAboveTheCeiling_WarnsThatCoverageIsPartial()
      {
        var configuration = ConfigurationLoader.LoadYaml(WideDocument(17));

        var result = ConfigurationValidator.Evaluate(configuration);

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(ConfigurationCheck.GuardExclusivity, warning.Check);
        Assert.Equal("/entries/main/policy/stages/0/to", warning.Pointer);
        Assert.Equal(
              "the state domain of the stage 'resolve' passes 65536 points, so check 5 sampled 65536 points at random and its coverage is partial",
              warning.Message);
      }

      // ---------------------------------------------------------------------------------------------
      // Check 6: reachability.
      // ---------------------------------------------------------------------------------------------
    [Fact]
    public void AnUnreachableStage_FailsCheckSixWithThePointerOfThatStage()
      {
        const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: greeter }
            entries:
              main:
                policy:
                  initial: greeting
                  stages:
                    - { id: greeting, agent: greeter, terminal: true }
                    - { id: island,   agent: greeter, terminal: true }
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.Reachability));

        Assert.Equal("/entries/main/policy/stages/1", error.Pointer);
        Assert.Equal("the stage 'island' is unreachable from the initial stage 'greeting' in entry 'main'", error.Message);
      }

    [Fact]
    public void ANonTerminalStageWithNoExit_FailsCheckSix()
      {
        const string document = """
              apiVersion: agentcore/v1
              agents:
                items:
                  - { id: greeter }
              entries:
                main:
                  policy:
                    initial: greeting
                    stages:
                      - { id: greeting, agent: greeter }
              """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.Reachability));

        Assert.Equal("/entries/main/policy/stages/0", error.Pointer);
        Assert.Equal("the stage 'greeting' is not terminal and has no exit", error.Message);
      }

      // ---------------------------------------------------------------------------------------------
      // Check 7: graph well-formedness.
      // ---------------------------------------------------------------------------------------------
    [Fact]
    public void AGraphWithNoStartNode_FailsCheckSeven()
      {
        const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: worker }
                - { id: closer }
            entries:
              main:
                graph:
                  nodes:
                    - { id: first,  agent: worker }
                    - { id: second, agent: closer, output: true }
                  edges:
                    - { from: first, to: second }

            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GraphWellFormedness));

        Assert.Equal("/entries/main/graph/nodes", error.Pointer);
        Assert.Equal("the graph in entry 'main' declares 0 start nodes, and check 7 needs exactly one", error.Message);
      }

    [Fact]
    public void AnOrphanNode_FailsCheckSevenWithThePointerOfThatNode()
      {
        const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: worker }
                - { id: closer }
            entries:
              main:
                graph:
                  nodes:
                    - { id: first,  agent: worker, start: true }
                    - { id: second, agent: closer, output: true }
                    - { id: island, agent: closer, output: true }
                  edges:
                    - { from: first, to: second }

            """;

          // An orphan is also unreachable, so check 6 speaks too. This test reads check 7 alone.
        var error = SingleFor(document, ConfigurationCheck.GraphWellFormedness);

        Assert.Equal("/entries/main/graph/nodes/2", error.Pointer);
        Assert.Equal("the node 'island' is an orphan: no edge reaches it and no edge leaves it", error.Message);
      }

    [Fact]
    public void APathThatReachesNoOutput_FailsCheckSeven()
      {
        const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: worker }
                - { id: closer }
            entries:
              main:
                graph:
                  nodes:
                    - { id: first,  agent: worker, start: true }
                    - { id: second, agent: closer, output: true }
                    - { id: sink,   agent: closer }
                  edges:
                    - { from: first,  to: second }
                    - { from: second, to: sink }

            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.GraphWellFormedness));

        Assert.Equal("/entries/main/graph/nodes/2", error.Pointer);
        Assert.Equal("no path from the node 'sink' reaches an output node", error.Message);
      }

    [Fact]
    public void AnUnreachableNode_FailsCheckSix()
      {
        const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: worker }
                - { id: closer }
            entries:
              main:
                graph:
                  nodes:
                    - { id: first,  agent: worker, start: true }
                    - { id: second, agent: closer, output: true }
                    - { id: island, agent: closer, output: true }
                  edges:
                    - { from: first,  to: second }
                    - { from: island, to: second }

            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.Reachability));

        Assert.Equal("/entries/main/graph/nodes/2", error.Pointer);
        Assert.Equal("the node 'island' is unreachable from the start node 'first' in entry 'main'", error.Message);
      }

      // ---------------------------------------------------------------------------------------------
      // Check 8: delegation cycles.
      // ---------------------------------------------------------------------------------------------
    [Fact]
    public void AnAgentAsToolLoop_FailsCheckEightWithThePointerOfTheToolThatClosesIt()
      {
        const string document = """
            apiVersion: agentcore/v1
            tools:
              - { id: call_writer,  kind: agent, agent: writer }
              - { id: call_planner, kind: agent, agent: planner }
            agents:
              items:
                - { id: planner, tools: [ call_writer ] }
                - { id: writer,  tools: [ call_planner ] }
            entries:
              main:
                agent: planner
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.DelegationCycles));

        Assert.Equal("/agents/items/1/tools/0", error.Pointer);
        Assert.Equal(
            "this tool runs the agent 'planner', and that closes the delegation cycle "
            + "planner -> writer -> planner. The call would never return.",
            error.Message);
    }

    [Fact]
    public void AnAgentThatCallsItself_FailsCheckEight()
    {
        const string document = """
            apiVersion: agentcore/v1
            tools:
              - { id: call_self, kind: agent, agent: planner }
            agents:
              items:
                - { id: planner, tools: [ call_self ] }
            entries:
              main:
                agent: planner
            """;

        var error = Assert.Single(Evaluate(document, ConfigurationCheck.DelegationCycles));

        Assert.Equal("/agents/items/0/tools/0", error.Pointer);
        Assert.Contains("planner -> planner", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATwoAgentDelegationChainWithNoLoop_PassesCheckEight()
    {
        const string document = """
            apiVersion: agentcore/v1
            tools:
              - { id: call_writer, kind: agent, agent: writer }
              - { id: call_editor, kind: agent, agent: editor }
            agents:
              items:
                - { id: planner, tools: [ call_writer ] }
                - { id: writer,  tools: [ call_editor ] }
                - { id: editor }
            entries:
              main:
                agent: planner
            """;

        Assert.Empty(ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(document)).Errors);
    }

    [Fact]
    public void AToolWhoseUsesValueMatchesAnAgentId_IsNotADelegationEdge()
    {
        // 'uses:' names a built-in and never an agent, so the match is a coincidence. The old check 8
        // read this pair as a cycle. The tool id matches an agent id here as well, and that is a
        // coincidence too.
        const string document = """
            apiVersion: agentcore/v1
            tools:
              - { id: writer,  kind: builtin, uses: planner }
              - { id: planner, kind: binding, binds: writer }
            agents:
              items:
                - { id: planner, tools: [ writer ] }
                - { id: writer,  tools: [ planner ] }
            entries:
              main:
                agent: writer
            """;

        Assert.Empty(ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(document)).Errors);
    }

    // ---------------------------------------------------------------------------------------------
    // The entry point.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void Validate_ThrowsOneExceptionThatCarriesEveryError()
    {
        const string document = """
            apiVersion: agentcore/v1
            state:
              stage: { type: string, writer: extractor }
            guards:
              ghost: { var: neverDeclared }
            agents:
              items:
                - { id: greeter }
            entries:
              main:
                policy:
                  initial: greeting
                  stages:
                    - { id: greeting, agent: ghost, terminal: true }

            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationValidator.Validate(configuration));

        Assert.Equal(3, failure.Errors.Count);
        Assert.Contains(failure.Errors, error => error.Check == ConfigurationCheck.ReferenceResolution);
        Assert.Contains(failure.Errors, error => error.Check == ConfigurationCheck.SlotWriters);
        Assert.Contains(failure.Errors, error => error.Check == ConfigurationCheck.GuardOperators);
      }

    [Fact]
    public void Validate_ReturnsTheWarningsWhenNothingFails()
      {
        var configuration = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);

        var result = ConfigurationValidator.Validate(configuration);

        Assert.True(result.IsValid);
      }

      // ---------------------------------------------------------------------------------------------
      // Decision 15: structural, then tool references.
      // ---------------------------------------------------------------------------------------------
      /// <summary>
      /// Decision 15's whole point: a YAML typo must not cost a round trip to every MCP server. The
      /// structural pass therefore has to find a defect that has nothing to do with tool ids, on a
      /// document whose tool references cannot possibly resolve yet.
      /// </summary>
    [Fact]
    public void EvaluateStructure_ADefectThatIsNotAToolReference_IsFoundWithNoServedIds()
      {
        const string document = """
            apiVersion: agentcore/v1
            guards:
              ghost: { var: neverDeclared }
            agents:
              items:
                - { id: planner, tools: [ jira.create_issue ] }
            entries:
              main:
                agent: planner
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        var result = ConfigurationValidator.EvaluateStructure(configuration);

        var error = Assert.Single(result.Errors);
        Assert.Equal(ConfigurationCheck.GuardOperators, error.Check);
        Assert.Equal("/guards/ghost", error.Pointer);
        Assert.Equal("the rule reads the slot 'neverDeclared', and state: does not declare it", error.Message);
        Assert.DoesNotContain(result.Errors, e => e.Message.Contains("jira.create_issue", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateToolReferences_AnIdNothingServes_FailsNamingTheIdAndThePointer()
    {
        const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: planner, tools: [ jira.create_issue ] }
            entries:
              main:
                agent: planner
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);
        var servedToolIds = new HashSet<string>(StringComparer.Ordinal);

        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationValidator.ValidateToolReferences(configuration, servedToolIds));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/agents/items/0/tools/0", error.Pointer);
        Assert.Equal(
            "nothing serves the tool 'jira.create_issue'. Declare it in tools:, or check that an mcp: server offers it.",
            error.Message);
    }

    [Fact]
    public void ValidateToolReferences_AnIdOnlyDiscoveryServes_Passes()
    {
        const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: planner, tools: [ jira.create_issue ] }
            entries:
              main:
                agent: planner
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);
        var servedToolIds = new HashSet<string>(StringComparer.Ordinal) { "jira.create_issue" };

        var exception = Record.Exception(() => ConfigurationValidator.ValidateToolReferences(configuration, servedToolIds));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateSkillReferences_ANameTheFolderDoesNotServe_FailsNamingItAndWhatIsServed()
    {
        const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: support, skills: [warranty-return] }
            entries:
              main:
                agent: support
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);
        var served = new HashSet<string>(["shipping-claims", "warranty-returns"], StringComparer.Ordinal);

        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationValidator.ValidateSkillReferences(configuration, served));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/agents/items/0/skills/0", error.Pointer);
        Assert.Equal(
            "the skill 'warranty-return' is not in the bound skills folder. The folder serves: shipping-claims, warranty-returns.",
            error.Message);
        Assert.Equal(ConfigurationCheck.ReferenceResolution, error.Check);
    }

    [Fact]
    public void ValidateSkillReferences_EveryNameServed_DoesNotThrow()
    {
        const string document = """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: support, skills: [warranty-returns] }
            entries:
              main:
                agent: support
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);
        var served = new HashSet<string>(["warranty-returns"], StringComparer.Ordinal);

        var exception = Record.Exception(() => ConfigurationValidator.ValidateSkillReferences(configuration, served));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("load_skill")]
    [InlineData("read_skill_resource")]
    [InlineData("run_skill_script")]
    public void ValidateSkillToolNames_AToolIdTheSkillsProviderOwns_FailsNamingIt(string reserved)
    {
        var document = $$"""
            apiVersion: agentcore/v1
            tools:
              - { id: {{reserved}}, kind: http, request: { method: GET, url: "https://example.test" } }
            agents:
              items:
                - { id: support, skills: [warranty-returns], tools: [{{reserved}}] }
            entries:
              main:
                agent: support
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationValidator.ValidateSkillToolNames(configuration));

        var error = Assert.Single(failure.Errors);
        Assert.Contains($"the tool id '{reserved}' is reserved", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSkillToolNames_NoAgentDeclaresSkills_AllowsTheName()
    {
        const string document = """
            apiVersion: agentcore/v1
            tools:
              - { id: load_skill, kind: http, request: { method: GET, url: "https://example.test" } }
            agents:
              items:
                - { id: support, tools: [load_skill] }
            entries:
              main:
                agent: support
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        var exception = Record.Exception(() => ConfigurationValidator.ValidateSkillToolNames(configuration));

        Assert.Null(exception);
    }

    /// <summary>Walks up from the test binaries to the directory that holds the solution file.</summary>
    /// <returns>The repository root.</returns>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentCore.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static ConfigurationError SingleFor(string yaml, ConfigurationCheck check)
    {
        var result = ConfigurationValidator.Evaluate(ConfigurationLoader.LoadYaml(yaml));

        return Assert.Single(result.Errors, error => error.Check == check);
    }

    private static IReadOnlyList<ConfigurationError> Evaluate(string yaml, ConfigurationCheck check)
    {
        var configuration = ConfigurationLoader.LoadYaml(yaml);
        var result = ConfigurationValidator.Evaluate(configuration);

        // Every error the document holds must belong to the check under test.
        Assert.All(result.Errors, error => Assert.Equal(check, error.Check));
        return result.Errors;
    }

    /// <summary>Writes a document whose sibling guards name enough boolean slots to pass the ceiling.</summary>
    /// <param name="slots">How many boolean slots to declare. 17 slots give 131,072 points.</param>
    /// <returns>The YAML text.</returns>
    private static string WideDocument(int slots)
    {
        var text = new StringBuilder();
        text.AppendLine("apiVersion: agentcore/v1");
        text.AppendLine("state:");
        for (var index = 0; index < slots; index++)
        {
            text.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  s{index}: {{ type: boolean, default: false, writer: extractor }}");
        }

        text.AppendLine("guards:");
        text.AppendLine("  first: { var: s0 }");
        text.Append("  second: { and: [ { \"!\": { var: s0 } }");
        for (var index = 1; index < slots; index++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $", {{ var: s{index} }}");
        }

        text.AppendLine(" ] }");
        text.AppendLine("agents:");
        text.AppendLine("  items:");
        text.AppendLine("    - { id: greeter }");
        text.AppendLine("entries:");
        text.AppendLine("  main:");
        text.AppendLine("    policy:");
        text.AppendLine("      initial: resolve");
        text.AppendLine("      stages:");
        text.AppendLine("        - id: resolve");
        text.AppendLine("          agent: greeter");
        text.AppendLine("          to:");
        text.AppendLine("            - { stage: close,    when: first }");
        text.AppendLine("            - { stage: escalate, when: second }");
        text.AppendLine("        - { id: close,    agent: greeter, terminal: true }");
        text.AppendLine("        - { id: escalate, agent: greeter, terminal: true }");
        return text.ToString();
    }
}

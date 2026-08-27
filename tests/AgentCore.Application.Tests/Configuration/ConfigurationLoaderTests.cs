using System.Text.Json;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Xunit;

namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// The section 8.1 document parses, and every section binds.
/// </summary>
public sealed class ConfigurationLoaderTests
{
    private static readonly AgentCoreConfiguration Example = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);

    [Fact]
    public void Example_CarriesTheDocumentHeader()
    {
        Assert.Equal(AgentCoreConfiguration.SupportedApiVersion, Example.ApiVersion);
        Assert.Equal("service-voice", Example.Name);
    }

    [Fact]
    public void Example_BindsEveryStateSlot()
    {
        Assert.Equal(6, Example.State.Count);

        var goodbye = Example.State["callerSaidGoodbye"];
        Assert.Equal(StateSlotType.Boolean, goodbye.Type);
        Assert.Equal(StateWriter.Extractor, goodbye.Writer);
        Assert.False(goodbye.Default!.GetValue<bool>());

        var counter = Example.State["failedResolveTurns"];
        Assert.Equal(StateSlotType.Integer, counter.Type);
        Assert.Equal(StateWriter.Counter, counter.Writer);
        Assert.Equal(0, counter.Default!.GetValue<int>());
        Assert.NotNull(counter.Increment);
        Assert.Contains("\"===\"", counter.Increment!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Example_BindsTheToolWriterPath()
    {
        var slot = Example.State["orderStatus"];

        Assert.Equal(StateWriter.Tool, slot.Writer);
        Assert.Equal(new ToolResultReference("lookup_order", "status"), slot.From);
        Assert.Null(slot.Default);
    }

    [Fact]
    public void Example_BindsTheExtractor()
    {
        Assert.NotNull(Example.Extractor);
        Assert.Equal("fill", Example.Extractor!.Model.Ref);
        Assert.Equal(ExtractorTrigger.AfterReply, Example.Extractor.When);
        Assert.Null(Example.Extractor.Model.Temperature);
    }

    [Fact]
    public void Example_KeepsEveryGuardAsRawJsonLogic()
    {
        Assert.Equal(5, Example.Guards.Count);
        Assert.Equal(
            ["saidGoodbye", "wantsHuman", "identified", "goodbyeOrFixed", "humanOrExhausted"],
            Example.Guards.Keys);

        Assert.Equal("""{"var":"callerSaidGoodbye"}""", Example.Guards["saidGoodbye"].ToJsonString());
    }

    [Fact]
    public void Example_BindsEveryToolKind()
    {
        Assert.Equal(3, Example.Tools.Count);

        Assert.Equal(ToolKind.Builtin, Example.Tools[0].Kind);
        Assert.Equal("ui.draw", Example.Tools[0].Uses);

        var binding = Example.Tools[2];
        Assert.Equal(ToolKind.Binding, binding.Kind);
        Assert.Equal("CreateCase", binding.Binds);
        Assert.NotNull(binding.Parameters);
        Assert.Equal("object", binding.Parameters!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Example_ReadsTheSecretReferenceAndResolvesNothing()
    {
        var http = Example.Tools[1];

        Assert.Equal(ToolKind.Http, http.Kind);
        Assert.NotNull(http.Request);
        Assert.Equal("GET", http.Request!.Method);
        Assert.Equal("https://api.example.com/orders/{orderId}", http.Request.Url);

        var header = http.Request.Headers["Authorization"];
        Assert.True(header.HasSecretReferences);
        Assert.Equal("Bearer ${secret:orders-api-key}", header.Raw);
        Assert.Equal("orders-api-key", Assert.Single(header.References).Name);
        Assert.Equal("Bearer opened", header.Format(_ => "opened"));
    }

    [Fact]
    public void Example_BindsAgents()
    {
        Assert.NotNull(Example.Agents);
        Assert.Equal("reply", Example.Agents!.Defaults!.Model!.Ref);
        Assert.Equal(0.3, Example.Agents.Defaults.Model.Temperature);
        Assert.StartsWith("<the stable cached prefix", Example.Agents.Defaults.Instructions, StringComparison.Ordinal);

        Assert.Equal(7, Example.Agents.Items.Count);
        Assert.Equal("resolver", Example.Agents.Items[2].Id);
        Assert.Empty(Example.Agents.Items[2].Tools);
        Assert.Empty(Example.Agents.Items[0].Tools);
    }

    [Fact]
    public void Example_BindsThePolicy()
    {
        Assert.NotNull(Example.Policy);
        Assert.Equal("greeting", Example.Policy!.Initial);
        Assert.Equal(5, Example.Policy.Stages.Count);

        var identify = Example.Policy.Stages[1];
        Assert.Equal("identifier", identify.Agent);
        Assert.Equal(StageNoMatch.Stay, identify.OnNoMatch);
        Assert.Equal(3, identify.To.Count);
        Assert.Equal("close", identify.To[0].Stage);
        Assert.Equal("saidGoodbye", identify.To[0].When!.Name);
        Assert.True(identify.To[0].When!.IsNamed);

        var close = Example.Policy.Stages[4];
        Assert.True(close.Terminal);
        Assert.Empty(close.To);

        Assert.Null(Example.Policy.Stages[0].To[0].When);
    }

    [Fact]
    public void Example_DeclaresNoGraph()
        => Assert.Null(Example.Graph);

    [Fact]
    public void Example_BindsProviders()
    {
        Assert.NotNull(Example.Providers);
        Assert.Equal(4, Example.Providers!.Llm.Count);
        Assert.Equal("gpt-4.1-mini", Example.Providers.Llm[0].Model);
        Assert.Equal("reply", Example.Providers.Llm[0].As);
        Assert.Equal("fill", Example.Providers.Llm[1].As);
        Assert.Equal("judge", Example.Providers.Llm[2].As);
        Assert.Equal("cheap", Example.Providers.Llm[3].As);
        Assert.Equal("telnyx-relay", Example.Providers.Speech!.Stt.Kind);
        Assert.Equal("telnyx-relay", Example.Providers.Speech.Tts.Kind);
        Assert.Equal("telnyx", Example.Providers.Telephony!.Kind);
        Assert.Equal("qdrant", Example.Providers.Knowledge!.Kind);
        Assert.Equal("https://qdrant.example.com:6334", Example.Providers.Knowledge.Endpoint);
        Assert.Equal(KnowledgeProviderConfiguration.DefaultCollection, Example.Providers.Knowledge.Collection);
    }

    [Fact]
    public void AKnowledgeProviderWithNoFields_TakesTheDefaults()
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

        var configuration = ConfigurationLoader.LoadYaml(document);

        var knowledge = configuration.Providers!.Knowledge!;
        Assert.Equal(KnowledgeProviderConfiguration.DefaultKind, knowledge.Kind);
        Assert.Null(knowledge.Endpoint);
        Assert.Equal(KnowledgeProviderConfiguration.DefaultCollection, knowledge.Collection);
    }

    [Fact]
    public void AKnowledgeFieldSetToNull_BindsNullAndKeepsTheOtherDefaults()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: foreign
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge:
                fields: { id: null, body: page_content }
            """;

        var knowledge = ConfigurationLoader.LoadYaml(document).Providers!.Knowledge!;

        Assert.Null(knowledge.Fields.Id);
        Assert.Equal("page_content", knowledge.Fields.Body);
        Assert.Equal(KnowledgeFieldsConfiguration.DefaultLexical, knowledge.Fields.Lexical);
        Assert.Equal(KnowledgeFieldsConfiguration.DefaultSource, knowledge.Fields.Source);
    }

    [Fact]
    public void ABodyFieldSetToNull_FailsTheLoad()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge:
                fields: { body: null }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains(
            failure.Errors,
            error => error.Pointer!.StartsWith("/providers/knowledge/fields", StringComparison.Ordinal));
    }

    [Fact]
    public void AKnowledgeProviderThatWritesEveryField_BindsThem()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: split
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge:
                kind: qdrant
                endpoint: https://cluster.example.com:6334
                collection: manuals
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        var knowledge = configuration.Providers!.Knowledge!;
        Assert.Equal("qdrant", knowledge.Kind);
        Assert.Equal("https://cluster.example.com:6334", knowledge.Endpoint);
        Assert.Equal("manuals", knowledge.Collection);
    }

    [Fact]
    public void AKnowledgeProviderWithNoVector_BindsNullMeaningTheAnonymousVector()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: plainvec
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge: {}
            """;

        var knowledge = ConfigurationLoader.LoadYaml(document).Providers!.Knowledge!;

        Assert.Null(knowledge.Vector);
    }

    [Fact]
    public void AKnowledgeProviderNamingAMapper_BindsIt()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: mapped
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge:
                mapper: acme-catalog
            """;

        Assert.Equal("acme-catalog", ConfigurationLoader.LoadYaml(document).Providers!.Knowledge!.Mapper);
    }

    [Fact]
    public void Example_BindsTheSpokenFallbackAndTheSampleRate()
    {
        // Both keys are optional, and the worked example writes both at their default.
        Assert.Equal(AgentCoreConfiguration.DefaultFallbackReply, Example.FallbackReply);
        Assert.NotNull(Example.Evaluation);
        Assert.Equal(EvaluationConfiguration.DefaultSampleRate, Example.Evaluation!.SampleRate);
    }

    [Fact]
    public void Example_BindsTheJudgeModelReference()
    {
        Assert.NotNull(Example.Evaluation!.Judge);
        Assert.Equal("judge", Example.Evaluation.Judge!.Ref);
        Assert.Equal(0, Example.Evaluation.Judge.Temperature);
    }

    [Fact]
    public void AnEvaluationSectionWithNoJudge_LeavesTheReferenceNull()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: quiet
            evaluation:
              sampleRate: 0.25
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        Assert.Null(configuration.Evaluation!.Judge);
    }

    [Fact]
    public void AJudgeWithNoRef_FailsTheLoad()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: broken
            evaluation:
              judge: { temperature: 0 }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains(failure.Errors, error => error.Pointer == "/evaluation/judge");
    }

    [Fact]
    public void ADocumentThatSetsBothTunableKeys_BindsThem()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: tuned
            fallbackReply: "One moment please. I will try that again."
            evaluation:
              sampleRate: 0.25
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        Assert.Equal("One moment please. I will try that again.", configuration.FallbackReply);
        Assert.Equal(0.25, configuration.Evaluation!.SampleRate);
    }

    [Fact]
    public void ADocumentThatOmitsBothTunableKeys_TakesTheDefaults()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: plain
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        // Section 8.7 still speaks a fallback, and T18 still evaluates no turn.
        Assert.Equal(AgentCoreConfiguration.DefaultFallbackReply, configuration.FallbackReply);
        Assert.Null(configuration.Evaluation);
    }

    [Fact]
    public void Example_BindsTheSpokenRefusal()
    {
        // The key is optional, and the worked example writes it at its default.
        Assert.Equal(AgentCoreConfiguration.DefaultRefusalReply, Example.RefusalReply);
    }

    [Fact]
    public void ADocumentThatSetsTheRefusalReply_BindsIt()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: guarded
            refusalReply: "I am not able to answer that."
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        Assert.Equal("I am not able to answer that.", configuration.RefusalReply);
    }

    [Fact]
    public void ADocumentThatOmitsTheRefusalReply_TakesTheDefault()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: plain
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        // A document written before the key existed behaves the same way.
        Assert.Equal(AgentCoreConfiguration.DefaultRefusalReply, configuration.RefusalReply);
    }

    [Fact]
    public void ADocumentThatSetsOnlyOneSpokenLine_LeavesTheOtherAtItsDefault()
    {
        var withFallback = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            name: tuned
            fallbackReply: "One moment please. I will try that again."
            """);
        var withRefusal = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            name: tuned
            refusalReply: "I am not able to answer that."
            """);

        Assert.Equal("One moment please. I will try that again.", withFallback.FallbackReply);
        Assert.Equal(AgentCoreConfiguration.DefaultRefusalReply, withFallback.RefusalReply);
        Assert.Equal(AgentCoreConfiguration.DefaultFallbackReply, withRefusal.FallbackReply);
        Assert.Equal("I am not able to answer that.", withRefusal.RefusalReply);
    }

    [Fact]
    public void ADocumentThatSetsBothSpokenLines_BindsThemIndependently()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: tuned
            fallbackReply: "One moment please. I will try that again."
            refusalReply: "I am not able to answer that."
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        Assert.Equal("One moment please. I will try that again.", configuration.FallbackReply);
        Assert.Equal("I am not able to answer that.", configuration.RefusalReply);
    }

    [Fact]
    public void AnEvaluationSectionWithNoRate_TakesTheDefaultRate()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: plain
            evaluation: {}
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        Assert.Equal(EvaluationConfiguration.DefaultSampleRate, configuration.Evaluation!.SampleRate);
    }

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("1", 1.0)]
    [InlineData("0.05", 0.05)]
    public void ASampleRateInsideTheRange_Binds(string written, double expected)
    {
        var configuration = ConfigurationLoader.LoadYaml(
            $"apiVersion: agentcore/v1\nname: plain\nevaluation:\n  sampleRate: {written}\n");

        Assert.Equal(expected, configuration.Evaluation!.SampleRate);
    }

    /// <summary>
    /// A repeated key is a mistake in both formats. On the YAML path YamlDotNet's own loader rejects
    /// two keys that are the same YAML node, before <see cref="YamlToJson"/> walks the mapping.
    /// </summary>
    [Fact]
    public void ADuplicateKeyInYaml_FailsTheLoad()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: plain
            name: again
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Contains(failure.Errors, error => error.Message.Contains("Duplicate key", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two keys that differ as YAML and agree as JSON are still one key in the document tree.
    /// </summary>
    /// <remarks>
    /// A tag makes two scalars different YAML nodes, so YamlDotNet keeps both — and both name the
    /// property "1" once the mapping becomes a <c>JsonObject</c>. The check in
    /// <c>YamlToJson.ConvertMapping</c> is what catches this, and it is the only thing that does:
    /// without it the second value silently replaces the first.
    /// </remarks>
    [Fact]
    public void TwoKeysThatDifferOnlyByTag_FailTheLoad()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: plain
            !!str 1: first
            1: second
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml(document));

        Assert.Equal(ConfigurationCheck.Syntax, failure.Check);
        Assert.Contains(failure.Errors, error => error.Message.Contains("appears twice", StringComparison.Ordinal));
    }

    /// <summary>
    /// A number no <see cref="double"/> can hold is a defect in the document, not a crash.
    /// </summary>
    /// <remarks>
    /// It parses to an infinity, which JSON cannot write. The loader used to let the
    /// <c>ArgumentException</c> that came of it straight out, past the
    /// <see cref="ConfigurationLoadException"/> section 8.7 tells every caller to catch.
    /// </remarks>
    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    public void ANumberTooLargeToHold_FailsTheLoad(string written)
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationLoader.LoadYaml($"apiVersion: agentcore/v1\nname: plain\nevaluation:\n  sampleRate: {written}\n"));

        Assert.Equal(ConfigurationCheck.Syntax, failure.Check);
        Assert.Contains(failure.Errors, error => error.Message.Contains("larger than a number can hold", StringComparison.Ordinal));
    }

    /// <summary>A number that underflows is zero, which JSON can write, so it loads.</summary>
    [Fact]
    public void ANumberTooSmallToHold_ReadsAsZero()
    {
        var configuration = ConfigurationLoader.LoadYaml(
            "apiVersion: agentcore/v1\nname: plain\nevaluation:\n  sampleRate: 1e-400\n");

        Assert.Equal(0, configuration.Evaluation!.SampleRate);
    }

    /// <summary>
    /// A repeated key on the JSON path is rejected by <c>ReadJson</c>'s own
    /// <see cref="System.Text.Json.JsonDocumentOptions.AllowDuplicateProperties"/> setting before the
    /// document ever reaches the shared reparse. This pins that behaviour too.
    /// </summary>
    [Fact]
    public void ADuplicateKeyInJson_FailsTheLoad()
    {
        const string document = """
            {
              "apiVersion": "agentcore/v1",
              "name": "plain",
              "name": "again"
            }
            """;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadJson(document));

        Assert.Contains(failure.Errors, error => error.Check == ConfigurationCheck.Syntax);
    }

    [Fact]
    public void Load_AToolWithAModelRef_ReadsIt()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: plain
            tools:
              - { id: draw, kind: builtin, uses: ui.draw, description: d, model: { ref: cheap } }
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        Assert.Equal("cheap", configuration.Tools[0].Model!.Ref);
    }

    [Fact]
    public void Load_AToolWithMaxRounds_ReadsIt()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: plain
            tools:
              - { id: draw, kind: builtin, uses: ui.draw, description: d, maxRounds: 4 }
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        Assert.Equal(4, configuration.Tools[0].MaxRounds);
    }

    [Fact]
    public void NoLinksBlock_LeavesLinksNullAndTheFeatureOff()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: unlinked
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge: {}
            """;

        Assert.Null(ConfigurationLoader.LoadYaml(document).Providers!.Knowledge!.Links);
    }

    [Fact]
    public void ALinksBlockWithoutLookup_DefaultsToFilter()
    {
        // filter is the only mode that works on any collection; uuid5 is now Spirit's explicit spelling.
        const string document = """
            apiVersion: agentcore/v1
            name: linked
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge:
                links: { field: related }
            """;

        var links = ConfigurationLoader.LoadYaml(document).Providers!.Knowledge!.Links;

        Assert.NotNull(links);
        Assert.Equal(KnowledgeLinkLookup.Filter, links!.Lookup);
        Assert.Equal("related", links.Field);
    }

    [Fact]
    public void ShippedExampleFile_Loads()
    {
        var path = Path.Combine(RepositoryRoot(), "demo", "AgentCore.Demo", "config", "example.yaml");
        Assert.True(File.Exists(path), $"The shipped example is missing at '{path}'.");

        var shipped = ConfigurationLoader.LoadFile(path);

        Assert.Equal(JsonSerializer.Serialize(Example), JsonSerializer.Serialize(shipped));
    }

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
}

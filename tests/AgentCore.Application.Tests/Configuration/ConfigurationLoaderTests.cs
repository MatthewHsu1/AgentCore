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
        Assert.Equal(6, Example.Tools.Count);

        Assert.Equal(ToolKind.Builtin, Example.Tools[0].Kind);
        Assert.Equal("knowledge.search", Example.Tools[0].Uses);

        var binding = Example.Tools[5];
        Assert.Equal(ToolKind.Binding, binding.Kind);
        Assert.Equal("CreateCase", binding.Binds);
        Assert.NotNull(binding.Parameters);
        Assert.Equal("object", binding.Parameters!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Example_ReadsTheSecretReferenceAndResolvesNothing()
    {
        var http = Example.Tools[4];

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

        Assert.Equal(5, Example.Agents.Items.Count);
        Assert.Equal("resolver", Example.Agents.Items[2].Id);
        Assert.Equal(["search_chunks", "read_doc", "list_docs", "grep_docs"], Example.Agents.Items[2].Tools);
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
        Assert.Equal(2, Example.Providers!.Llm.Count);
        Assert.Equal("gpt-4.1-mini", Example.Providers.Llm[0].Model);
        Assert.Equal("reply", Example.Providers.Llm[0].As);
        Assert.Equal("fill", Example.Providers.Llm[1].As);
        Assert.Equal("telnyx-relay", Example.Providers.Speech!.Kind);
        Assert.Equal("telnyx", Example.Providers.Telephony!.Kind);
        Assert.Equal("filesystem", Example.Providers.Knowledge!.Search);
        Assert.Equal("filesystem", Example.Providers.Knowledge.Documents);
        Assert.Equal("./kb", Example.Providers.Knowledge.Root);
        Assert.Null(Example.Providers.Knowledge.Endpoint);
        Assert.Equal(KnowledgeProviderConfiguration.DefaultCollection, Example.Providers.Knowledge.Collection);
    }

    [Fact]
    public void AKnowledgeProviderWithNoFields_TakesTheDefaults()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: plain
            providers:
              knowledge: {}
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        var knowledge = configuration.Providers!.Knowledge!;
        Assert.Equal(KnowledgeProviderConfiguration.DefaultSearch, knowledge.Search);
        Assert.Equal(KnowledgeProviderConfiguration.DefaultDocuments, knowledge.Documents);
        Assert.Equal(KnowledgeProviderConfiguration.DefaultRoot, knowledge.Root);
        Assert.Null(knowledge.Endpoint);
        Assert.Equal(KnowledgeProviderConfiguration.DefaultCollection, knowledge.Collection);
    }

    [Fact]
    public void AKnowledgeProviderThatWritesEveryField_BindsThem()
    {
        const string document = """
            apiVersion: agentcore/v1
            name: split
            providers:
              knowledge:
                search: zilliz
                documents: filesystem
                root: ./docs
                endpoint: https://cluster.example.com
                collection: manuals
            """;

        var configuration = ConfigurationLoader.LoadYaml(document);

        var knowledge = configuration.Providers!.Knowledge!;
        Assert.Equal("zilliz", knowledge.Search);
        Assert.Equal("filesystem", knowledge.Documents);
        Assert.Equal("./docs", knowledge.Root);
        Assert.Equal("https://cluster.example.com", knowledge.Endpoint);
        Assert.Equal("manuals", knowledge.Collection);
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

    [Fact]
    public void ShippedExampleFile_Loads()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "AgentCore.Api", "config", "example.yaml");
        Assert.True(File.Exists(path), $"The shipped example is missing at '{path}'.");

        var shipped = ConfigurationLoader.LoadFile(path);

        Assert.Equal(Example, shipped);
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

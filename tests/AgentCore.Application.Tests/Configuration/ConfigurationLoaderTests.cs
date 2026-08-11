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
        Assert.Equal(4, Example.Tools.Count);

        Assert.Equal(ToolKind.Builtin, Example.Tools[0].Kind);
        Assert.Equal("knowledge.search", Example.Tools[0].Uses);

        var binding = Example.Tools[3];
        Assert.Equal(ToolKind.Binding, binding.Kind);
        Assert.Equal("CreateCase", binding.Binds);
        Assert.NotNull(binding.Parameters);
        Assert.Equal("object", binding.Parameters!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Example_ReadsTheSecretReferenceAndResolvesNothing()
    {
        var http = Example.Tools[2];

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
        Assert.Equal(["search_chunks", "read_doc"], Example.Agents.Items[2].Tools);
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
        Assert.Equal("zilliz", Example.Providers.Knowledge!.Store);
        Assert.Equal("./kb", Example.Providers.Knowledge.Root);
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

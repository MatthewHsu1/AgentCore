using System.Text.Json;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Xunit;

namespace AgentCore.Application.Tests.Configuration
{
    /// <summary>
    /// The worked example document parses, and every section binds.
    /// </summary>
    public sealed class ConfigurationLoaderTests
    {
        private static readonly AgentCoreConfiguration Example = ConfigurationLoader.LoadYaml(ExampleDocument.Yaml);

        [Fact]
        public void Example_CarriesTheDocumentHeader()
        {
            Assert.Equal(AgentCoreConfiguration.SupportedApiVersion, Example.ApiVersion);
            Assert.Equal(["phone", "chat"], [.. Example.Entries.Keys]);
        }

        [Fact]
        public void Example_BindsEveryStateSlot()
        {
            Assert.Equal(8, Example.State.Count);

            StateSlotConfiguration goodbye = Example.State["callerSaidGoodbye"];
            Assert.Equal(StateSlotType.Boolean, goodbye.Type);
            Assert.Equal(StateWriter.Extractor, goodbye.Writer);
            Assert.False(goodbye.Default!.GetValue<bool>());

            StateSlotConfiguration counter = Example.State["failedResolveTurns"];
            Assert.Equal(StateSlotType.Integer, counter.Type);
            Assert.Equal(StateWriter.Counter, counter.Writer);
            Assert.Equal(0, counter.Default!.GetValue<int>());
            Assert.NotNull(counter.Increment);
            Assert.Contains("\"===\"", counter.Increment!.ToJsonString(), StringComparison.Ordinal);
        }

        [Fact]
        public void Example_BindsTheToolWriterPath()
        {
            StateSlotConfiguration slot = Example.State["orderStatus"];

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

            Assert.Equal(/*lang=json,strict*/ """{"var":"callerSaidGoodbye"}""", Example.Guards["saidGoodbye"].ToJsonString());
        }

        [Fact]
        public void Example_BindsEveryToolKind()
        {
            Assert.Equal(4, Example.Tools.Count);

            ToolConfiguration binding = Example.Tools[1];
            Assert.Equal(ToolKind.Binding, binding.Kind);
            Assert.Equal("CreateCase", binding.Binds);
            Assert.NotNull(binding.Parameters);
            Assert.Equal("object", binding.Parameters!["type"]!.GetValue<string>());

            Assert.Equal(ToolKind.Builtin, Example.Tools[2].Kind);
            Assert.Equal("web.search", Example.Tools[2].Uses);

            Assert.Equal(ToolKind.Builtin, Example.Tools[3].Kind);
            Assert.Equal("file.publish", Example.Tools[3].Uses);
        }

        [Fact]
        public void Example_ReadsTheSecretReferenceAndResolvesNothing()
        {
            ToolConfiguration http = Example.Tools[0];

            Assert.Equal(ToolKind.Http, http.Kind);
            Assert.NotNull(http.Request);
            Assert.Equal("GET", http.Request!.Method);
            Assert.Equal("https://api.example.com/orders/{orderId}", http.Request.Url);

            SecretTemplate header = http.Request.Headers["Authorization"];
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
            PolicyConfiguration? policy = Example.Entries["phone"].Policy;
            Assert.NotNull(policy);
            Assert.Equal("greeting", policy!.Initial);
            Assert.Equal(5, policy.Stages.Count);

            StageConfiguration identify = policy.Stages[1];
            Assert.Equal("identifier", identify.Agent);
            Assert.Equal(StageNoMatch.Stay, identify.OnNoMatch);
            Assert.Equal(3, identify.To.Count);
            Assert.Equal("close", identify.To[0].Stage);
            Assert.Equal("saidGoodbye", identify.To[0].When!.Name);
            Assert.True(identify.To[0].When!.IsNamed);

            StageConfiguration close = policy.Stages[4];
            Assert.True(close.Terminal);
            Assert.Empty(close.To);

            Assert.Null(policy.Stages[0].To[0].When);
        }

        [Fact]
        public void Example_BindsTheChatEntry()
        {
            Assert.Equal("webchat", Example.Entries["chat"].Agent);
        }

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
            Assert.Equal(false, Example.Providers.Llm[3].WebSearch);
            Assert.Equal("telnyx-relay", Example.Providers.Speech!.Stt.Kind);
            Assert.Equal("qdrant", Example.Providers.Knowledge!.Kind);
            Assert.Equal("https://qdrant.example.com:6334", Example.Providers.Knowledge.Endpoint);
            Assert.Equal("kb", Example.Providers.Knowledge.Collection);
        }

        [Fact]
        public void Load_AToolWithAModelRef_ReadsIt()
        {
            const string document = """
            apiVersion: agentcore/v1
            tools:
              - { id: search, kind: builtin, uses: web.search, description: d, model: { ref: cheap } }
            agents:
              items:
                - { id: search, instructions: "ok" }
            entries:
              main:
                agent: search
            """;

            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(document);

            Assert.Equal("cheap", configuration.Tools[0].Model!.Ref);
        }

        [Fact]
        public void Load_AToolWithMaxRounds_ReadsIt()
        {
            const string document = """
            apiVersion: agentcore/v1
            tools:
              - { id: dummy, kind: builtin, uses: web.search, description: d, maxRounds: 4 }
            agents:
              items:
                - { id: only, instructions: "ok" }
            entries:
              main:
                agent: only
            """;

            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(document);

            Assert.Equal(4, configuration.Tools[0].MaxRounds);
        }

        [Fact]
        public void ShippedExampleFile_Loads()
        {
            string path = Path.Combine(RepositoryRoot(), "config", "example.yaml");
            Assert.True(File.Exists(path), $"The shipped example is missing at '{path}'.");

            AgentCoreConfiguration shipped = ConfigurationLoader.LoadFile(path);

            Assert.Equal(JsonSerializer.Serialize(Example), JsonSerializer.Serialize(shipped));
        }

        private static string RepositoryRoot()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentCore.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }
    }
}

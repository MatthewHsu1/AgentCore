using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Application.Tools.Shipped;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// <see cref="ShippedAgentBuilder"/> is the one seam every shipped agent goes through to become the
/// function an outer agent calls. These tests pin the document/definition split: a shipped agent
/// cannot name its own model, only the document can override the round cap and the description, and
/// a boot with an unbound port — the one it always needs (<see cref="IChatClientFactory"/>) or one a
/// definition names of its own — fails naming both the tool id and the port.
/// </summary>
public sealed class ShippedAgentBuilderTests
{
    private static ToolConfiguration Declared(string id) => new()
    {
        Id = id,
        Kind = ToolKind.Builtin,
        Uses = id,
    };

    [Fact]
    public void Build_NoChatClientFactory_FailsTheBootNamingThePort()
    {
        var ports = new BuiltinToolPorts(null);

        var error = Assert.Throws<ConfigurationLoadException>(
            () => ShippedAgentBuilder.Build(new FakeDefinition(), Declared("draw"), ports));

        Assert.Contains("IChatClientFactory", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DefinitionNamesAMissingPort_FailsNamingTheToolAndThePort()
    {
        var definition = new FakeDefinition(missingPort: "IKnowledgeRetrievalPort");

        var error = Assert.Throws<ConfigurationLoadException>(
            () => ShippedAgentBuilder.Build(
                definition, Declared("draw"), new BuiltinToolPorts(new RecordingChatClientFactory())));

        Assert.Contains("draw", error.Message, StringComparison.Ordinal);
        Assert.Contains("IKnowledgeRetrievalPort", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AModelRefOnTheDeclaration_IsWhatTheFactoryIsAsked()
    {
        RecordingChatClientFactory factory = new();

        ShippedAgentBuilder.Build(
            new FakeDefinition(),
            Declared("draw") with { Model = new ModelReference { Ref = "cheap" } },
            new BuiltinToolPorts(factory));

        Assert.Equal("cheap", factory.Asked!.Ref);
    }

    [Fact]
    public void Build_NoModelRef_AsksTheFactoryForItsDefault()
    {
        RecordingChatClientFactory factory = new();

        ShippedAgentBuilder.Build(new FakeDefinition(), Declared("draw"), new BuiltinToolPorts(factory));

        Assert.Null(factory.Asked);
    }

    [Fact]
    public void Build_TheDocumentWritesNoDescription_UsesTheDefinitionsDefault()
    {
        var function = ShippedAgentBuilder.Build(
            new FakeDefinition(),
            Declared("draw") with { Description = null },
            new BuiltinToolPorts(new RecordingChatClientFactory()));

        Assert.Equal(FakeDefinition.Description, function.Description);
    }

    /// <summary>
    /// The round cap reaches the real <c>MaximumIterationsPerRequest</c> the inner loop obeys:
    /// <see cref="LoopingToolCallingChatClient"/> never answers with text, only ever a tool call, so
    /// the tool body keeps running until the cap gives up — the same measurement
    /// <c>docs/probes/ShippedAgentProbe</c> made of <c>MaximumIterationsPerRequest</c> directly. (The
    /// client's own request count runs one higher than the cap: the loop's last request comes back as
    /// one more tool call that the cap then refuses to invoke, so the tool body itself, not the
    /// request count, is what the cap actually bounds.)
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Build_DocumentMaxRounds_CapsTheInnerLoopAtThatValue(int rounds)
    {
        var calls = 0;
        var loopTool = AIFunctionFactory.Create(() => { calls++; return "keep going"; }, "loop_tool");
        var definition = new FakeDefinition(innerTools: [loopTool]);

        var function = ShippedAgentBuilder.Build(
            definition,
            Declared("draw") with { MaxRounds = rounds },
            new BuiltinToolPorts(new RecordingChatClientFactory(new LoopingToolCallingChatClient())));

        await function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "go" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(rounds, calls);
    }

    [Fact]
    public async Task Build_NoMaxRounds_CapsTheInnerLoopAtTheDefinitionsDefault()
    {
        var calls = 0;
        var loopTool = AIFunctionFactory.Create(() => { calls++; return "keep going"; }, "loop_tool");
        var definition = new FakeDefinition(innerTools: [loopTool]);

        var function = ShippedAgentBuilder.Build(
            definition,
            Declared("draw"),
            new BuiltinToolPorts(new RecordingChatClientFactory(new LoopingToolCallingChatClient())));

        await function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "go" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(FakeDefinition.DefaultRounds, calls);
    }

    /// <summary>
    /// The spec's failure rules put a spent round cap alongside a dead MCP server: a section 8.7
    /// error result, never a throw and never silence. <c>AsAIFunction</c> on its own returns the
    /// final text of the response the cap stopped on, and
    /// <see cref="PresentCallingChatClient"/> asks for a tool on every request including the
    /// tool-less last one, so that text is the empty string an outer agent reads as success.
    /// </summary>
    [Fact]
    public async Task Build_TheInnerLoopSpendsEveryRound_AnswersAnErrorNamingTheToolAndTheCap()
    {
        const string Tree = """{ "$type": "Card" }""";

        var definition = new FakeDefinition(
            innerTools: [AIFunctionFactory.Create((JsonElement tree) => "ok", "present")]);

        var function = ShippedAgentBuilder.Build(
            definition,
            Declared("draw") with { MaxRounds = 2 },
            new BuiltinToolPorts(new RecordingChatClientFactory(new PresentCallingChatClient(Tree, Tree, Tree))));

        var result = await function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "go" }),
            TestContext.Current.CancellationToken);

        var error = Assert.IsType<JsonObject>(result);
        Assert.True(ToolErrorResult.IsError(error));
        Assert.Equal("draw", error[ToolErrorResult.ToolProperty]!.GetValue<string>());
        Assert.Contains("2", error[ToolErrorResult.MessageProperty]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Reaching the cap is not the same as being cut off mid-sentence: MEAI spends one last request
    /// with the tools removed, so a model that will answer in words still gets the last word.
    /// <see cref="LoopingToolCallingChatClient"/> answers <c>{}</c> exactly when it is offered no
    /// tool, which is what makes that round visible here. Words are handed back untouched.
    /// </summary>
    [Fact]
    public async Task Build_TheInnerAgentAnswersTheToollessLastRound_HandsThoseWordsBackUntouched()
    {
        var definition = new FakeDefinition(
            innerTools: [AIFunctionFactory.Create(() => "keep going", "loop_tool")]);

        var function = ShippedAgentBuilder.Build(
            definition,
            Declared("draw") with { MaxRounds = 2 },
            new BuiltinToolPorts(new RecordingChatClientFactory(new LoopingToolCallingChatClient())));

        var result = await function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "go" }),
            TestContext.Current.CancellationToken);

        Assert.Equal("{}", Assert.IsType<JsonElement>(result).GetString());
    }

    /// <summary>
    /// The knowledge ports say an adapter may throw, and a shipped agent's inner tool calls one
    /// directly. The auditing loop turns a fault the model can answer into a result rather than an
    /// exception, so the framework's consecutive-error budget — three, and lower than a shipped
    /// agent's round cap can be — is never spent on it. Without the loop the fourth throw ends the
    /// call, and the inner agent never gets its remaining rounds.
    /// </summary>
    [Fact]
    public async Task Build_EveryRoundThrowsAFaultTheModelCanAnswer_TheInnerLoopStillSpendsEveryRound()
    {
        const int Rounds = 6;

        var calls = 0;
        var throwing = AIFunctionFactory.Create(
            () =>
            {
                calls++;
                throw new InvalidOperationException("the store said no");
            },
            "loop_tool");

        var function = ShippedAgentBuilder.Build(
            new FakeDefinition(innerTools: [throwing]),
            Declared("draw") with { MaxRounds = Rounds },
            new BuiltinToolPorts(new RecordingChatClientFactory(new LoopingToolCallingChatClient())));

        await function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "go" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(Rounds, calls);
    }

    /// <summary>
    /// The other half of section 8.7. A fault naming a dependency that is not there must still
    /// propagate, so the outer agent's error budget counts it and the turn can end on the fallback
    /// line. It must also be reported: a plain loop never calls
    /// <see cref="ToolFailureScope.Report"/> at all, so before this an inner tool could take the
    /// knowledge store down mid-call and the audit chain would hold nothing about it.
    /// </summary>
    [Fact]
    public async Task Build_AnInnerToolThrowsAFaultBeyondTheModel_ItIsReportedAndPropagates()
    {
        var throwing = AIFunctionFactory.Create(
            () =>
            {
                throw new HttpRequestException("the store is not answering");
            },
            "loop_tool");

        var function = ShippedAgentBuilder.Build(
            new FakeDefinition(innerTools: [throwing]),
            Declared("draw") with { MaxRounds = 4 },
            new BuiltinToolPorts(new RecordingChatClientFactory(new LoopingToolCallingChatClient())));

        List<ToolFailure> reported = [];
        using var scope = ToolFailureScope.Enter(reported.Add);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => function.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "go" }),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Contains(reported, failure => failure.ToolName == "loop_tool");
    }

    /// <summary>A shipped agent whose missing port and inner tools a test controls, so these tests can
    /// isolate each of <see cref="ShippedAgentBuilder"/>'s two port checks and the round cap in turn.</summary>
    private sealed class FakeDefinition : IShippedAgentDefinition
    {
        public const string Description = "the fake shipped agent's own default description.";

        public const int DefaultRounds = 5;

        private readonly string? _missingPort;
        private readonly IReadOnlyList<AITool> _innerTools;

        public FakeDefinition(string? missingPort = null, IReadOnlyList<AITool>? innerTools = null)
        {
            _missingPort = missingPort;
            _innerTools = innerTools ?? [];
        }

        public string Name => "fake";

        public string DefaultDescription => Description;

        public string Instructions => "be a fake shipped agent.";

        public int DefaultMaxRounds => DefaultRounds;

        public IReadOnlyList<AITool> InnerTools(ToolConfiguration tool, BuiltinToolPorts ports) => _innerTools;

        public string? MissingPort(BuiltinToolPorts ports) => _missingPort;
    }
}

using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// The third tool kind of section 8.1: <c>kind: binding</c>, which calls a host delegate.
/// </summary>
/// <remarks>
/// The document writes <c>binds: CreateCase</c> and nothing else. The host registers the delegate
/// that name points at, so the tool crosses no port and reaches no vendor.
/// </remarks>
public sealed class BindingToolTests
{
    private static readonly ToolConfiguration CreateCase = new()
    {
        Id = "create_case",
        Kind = ToolKind.Binding,
        Binds = "CreateCase",
        Description = "Open a service case for a human agent.",
        Parameters = JsonNode.Parse("""{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"]}"""),
    };

    // ---------------------------------------------------------------------------------------------
    // The registry.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void TheRegistry_HoldsWhatTheHostRegisters()
    {
        ToolBindingRegistry registry = new();
        registry.Register("CreateCase", (arguments, cancellationToken) => ValueTask.FromResult<object?>(null));

        Assert.Equal(1, registry.Count);
        Assert.True(registry.Contains("CreateCase"));
        Assert.False(registry.Contains("createcase"));
    }

    [Fact]
    public void TheSameNameTwice_FailsAtStartup()
    {
        ToolBindingRegistry registry = new();
        registry.Register("CreateCase", (arguments, cancellationToken) => ValueTask.FromResult<object?>(null));

        Assert.Throws<ArgumentException>(
            () => registry.Register("CreateCase", (arguments, cancellationToken) => ValueTask.FromResult<object?>(null)));
    }

    // ---------------------------------------------------------------------------------------------
    // Calling.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheToolCallsTheDelegateTheHostRegistered()
    {
        JsonObject? seen = null;
        ToolBindingRegistry registry = new();
        registry.Register("CreateCase", (arguments, cancellationToken) =>
        {
            seen = arguments;
            return ValueTask.FromResult<object?>(JsonNode.Parse("""{"caseId":"C-1"}"""));
        });

        var result = await CallAsync(new BindingToolFactory(registry).Create(CreateCase), ("summary", "broken belt"));

        Assert.NotNull(seen);
        Assert.Equal("broken belt", seen["summary"]!.GetValue<string>());
        Assert.Equal("C-1", Assert.IsType<JsonObject>(result)["caseId"]!.GetValue<string>());
    }

    [Fact]
    public void TheDeclaredSchema_ReachesTheModelUnchanged()
    {
        ToolBindingRegistry registry = new();
        registry.Register("CreateCase", (arguments, cancellationToken) => ValueTask.FromResult<object?>(null));

        var function = Assert.IsAssignableFrom<AIFunction>(new BindingToolFactory(registry).Create(CreateCase));

        Assert.Equal("create_case", function.Name);
        Assert.Equal("Open a service case for a human agent.", function.Description);
        Assert.True(JsonNode.DeepEquals(CreateCase.Parameters, JsonNode.Parse(function.JsonSchema.GetRawText())));
    }

    // ---------------------------------------------------------------------------------------------
    // Section 8.7: a tool returns an error result and does not throw.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AHostDelegateThatThrows_BecomesAnErrorResult()
    {
        ToolBindingRegistry registry = new();
        registry.Register("CreateCase", (arguments, cancellationToken)
            => throw new InvalidOperationException("the case system is down"));

        var result = await CallAsync(new BindingToolFactory(registry).Create(CreateCase), ("summary", "broken belt"));

        var error = Assert.IsType<JsonObject>(result);
        Assert.True(ToolErrorResult.IsError(error));
        Assert.Equal("create_case", error["tool"]!.GetValue<string>());
        Assert.Contains("the case system is down", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostDelegateThatReturnsNothing_ReturnsAnEmptyResultAndNotAFailure()
    {
        ToolBindingRegistry registry = new();
        registry.Register("CreateCase", (arguments, cancellationToken) => ValueTask.FromResult<object?>(null));

        var result = await CallAsync(new BindingToolFactory(registry).Create(CreateCase), ("summary", "broken belt"));

        Assert.Null(result);
    }

    // ---------------------------------------------------------------------------------------------
    // Failing at startup.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void ABindsNameTheHostNeverRegistered_FailsAtStartup()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => new BindingToolFactory(new ToolBindingRegistry()).Create(CreateCase));

        Assert.Equal(ConfigurationCheck.ReferenceResolution, failure.Check);
        Assert.Contains("CreateCase", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBindingFactory_ServesNoOtherKind()
    {
        BindingToolFactory factory = new(new ToolBindingRegistry());

        Assert.Null(factory.Create(new ToolConfiguration { Id = "s", Kind = ToolKind.Builtin, Uses = "knowledge.read" }));
        Assert.Null(factory.Create(new ToolConfiguration { Id = "i", Kind = ToolKind.Agent, Agent = "a" }));
    }

    private static async Task<object?> CallAsync(AITool? tool, params (string Name, object? Value)[] arguments)
    {
        var function = Assert.IsAssignableFrom<AIFunction>(tool);

        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            values[argument.Name] = argument.Value;
        }

        return await function.InvokeAsync(new AIFunctionArguments(values), TestContext.Current.CancellationToken);
    }
}

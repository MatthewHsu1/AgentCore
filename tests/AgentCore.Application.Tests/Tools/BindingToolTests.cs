using System.Text.Json;
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

    private static readonly string[] Tags = ["belt", "motor"];

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

        var result = await CallAsync(await CreateAsync(registry, CreateCase), ("summary", "broken belt"));

        Assert.NotNull(seen);
        Assert.Equal("broken belt", seen["summary"]!.GetValue<string>());
        Assert.Equal("C-1", Assert.IsType<JsonObject>(result)["caseId"]!.GetValue<string>());
    }

    [Fact]
    public async Task EachArgumentTypeTheSwitchNames_ArrivesAsThatJsonType()
    {
        JsonObject? seen = null;
        ToolBindingRegistry registry = new();
        registry.Register("CreateCase", (arguments, cancellationToken) =>
        {
            seen = arguments;
            return ValueTask.FromResult<object?>(null);
        });

        await CallAsync(
            await CreateAsync(registry, CreateCase),
            ("summary", "broken belt"),
            ("node", JsonNode.Parse("""{"a":1}""")),
            ("element", JsonDocument.Parse("""[1,2]""").RootElement),
            ("flag", true),
            ("count", 7),
            ("ticks", 9_000_000_000L),
            ("ratio", 1.5d),
            ("money", 2.25m),
            ("nothing", null));

        Assert.NotNull(seen);
        Assert.Equal("broken belt", seen["summary"]!.GetValue<string>());
        Assert.Equal("""{"a":1}""", seen["node"]!.ToJsonString());
        Assert.Equal("[1,2]", seen["element"]!.ToJsonString());
        Assert.True(seen["flag"]!.GetValue<bool>());
        Assert.Equal(7, seen["count"]!.GetValue<int>());
        Assert.Equal(9_000_000_000L, seen["ticks"]!.GetValue<long>());
        Assert.Equal(1.5d, seen["ratio"]!.GetValue<double>());
        Assert.Equal(2.25m, seen["money"]!.GetValue<decimal>());
        Assert.Null(seen["nothing"]);
    }

    // ---------------------------------------------------------------------------------------------
    // The fallback arm. A type the switch does not name is carried as the text of ToString(), and
    // not serialized. Any replacement for the switch has to keep answering this way.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AnArgumentTypeTheSwitchDoesNotName_ArrivesAsItsToStringText()
    {
        JsonObject? seen = null;
        ToolBindingRegistry registry = new();
        registry.Register("CreateCase", (arguments, cancellationToken) =>
        {
            seen = arguments;
            return ValueTask.FromResult<object?>(null);
        });

        await CallAsync(
            await CreateAsync(registry, CreateCase),
            ("tags", Tags),
            ("id", Guid.Parse("2f1b7d64-0f4a-4f0a-9c1c-2b0b6a2b7c11")),
            ("ratio", 1.5f));

        Assert.NotNull(seen);
        Assert.Equal("System.String[]", seen["tags"]!.GetValue<string>());
        Assert.Equal("2f1b7d64-0f4a-4f0a-9c1c-2b0b6a2b7c11", seen["id"]!.GetValue<string>());
        Assert.Equal(JsonValueKind.String, seen["ratio"]!.GetValueKind());
    }

    [Fact]
    public async Task TheDeclaredSchema_ReachesTheModelUnchanged()
    {
        ToolBindingRegistry registry = new();
        registry.Register("CreateCase", (arguments, cancellationToken) => ValueTask.FromResult<object?>(null));

        var function = Assert.IsAssignableFrom<AIFunction>(await CreateAsync(registry, CreateCase));

        Assert.Equal("create_case", function.Name);
        Assert.Equal("Open a service case for a human agent.", function.Description);
        Assert.True(JsonNode.DeepEquals(CreateCase.Parameters, JsonNode.Parse(function.JsonSchema.GetRawText())));
    }

    // ---------------------------------------------------------------------------------------------
    // Section 8.7: a tool returns an error result and does not throw. Task 7a moved the
    // classification that makes that true off DeclaredTool and into
    // AuditingFunctionInvokingChatClient, so calling the bare tool directly now sees the exception
    // the host delegate threw. See AuditingFunctionInvokingChatClientErrorPolicyTests and
    // CallSessionTests for the end-to-end guarantee.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AHostDelegateThatThrows_PropagatesForTheMiddlewareToClassify()
    {
        ToolBindingRegistry registry = new();
        registry.Register("CreateCase", (arguments, cancellationToken)
            => throw new InvalidOperationException("the case system is down"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await CallAsync(await CreateAsync(registry, CreateCase), ("summary", "broken belt")));

        Assert.Equal("the case system is down", thrown.Message);
    }

    [Fact]
    public async Task AHostDelegateThatReturnsNothing_ReturnsAnEmptyResultAndNotAFailure()
    {
        ToolBindingRegistry registry = new();
        registry.Register("CreateCase", (arguments, cancellationToken) => ValueTask.FromResult<object?>(null));

        var result = await CallAsync(await CreateAsync(registry, CreateCase), ("summary", "broken belt"));

        Assert.Null(result);
    }

    // ---------------------------------------------------------------------------------------------
    // Failing at startup.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task ABindsNameTheHostNeverRegistered_FailsAtStartup()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await CreateAsync(new ToolBindingRegistry(), CreateCase));

        Assert.Equal(ConfigurationCheck.ReferenceResolution, failure.Check);
        Assert.Contains("CreateCase", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBindingSource_ServesNoOtherKind()
    {
        ToolBindingRegistry registry = new();

        Assert.Null(await CreateAsync(registry, new ToolConfiguration { Id = "s", Kind = ToolKind.Builtin, Uses = "knowledge.read" }));
        Assert.Null(await CreateAsync(registry, new ToolConfiguration { Id = "i", Kind = ToolKind.Agent, Agent = "a" }));
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

    private static async Task<AITool?> CreateAsync(ToolBindingRegistry registry, ToolConfiguration tool)
    {
        BindingToolSource source = new(registry);
        var context = new ToolSourceContext(new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Tools = [tool],
        });

        var registrations = await source.ProvideAsync(context, TestContext.Current.CancellationToken);
        return registrations.Count == 0 ? null : registrations[0].Materialise();
    }

    [Fact]
    public async Task AnUnregisteredBindsName_FailsTheBoot()
    {
        ToolBindingRegistry bindings = new();
        BindingToolSource source = new(bindings);
        var context = new ToolSourceContext(new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Tools = [new ToolConfiguration { Id = "open_case", Kind = ToolKind.Binding, Binds = "CreateCase" }],
        });

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(async () =>
            await source.ProvideAsync(context, TestContext.Current.CancellationToken));

        Assert.Contains("CreateCase", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARegisteredBinding_BecomesOneRegistration()
    {
        ToolBindingRegistry bindings = new();
        bindings.Register("CreateCase", (_, _) => ValueTask.FromResult<object?>("done"));
        BindingToolSource source = new(bindings);
        var context = new ToolSourceContext(new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Tools =
            [
                new ToolConfiguration
                {
                    Id = "open_case", Kind = ToolKind.Binding, Binds = "CreateCase", Description = "Open a case.",
                },
            ],
        });

        var registrations = await source.ProvideAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal("open_case", Assert.Single(registrations).Id);
    }
}

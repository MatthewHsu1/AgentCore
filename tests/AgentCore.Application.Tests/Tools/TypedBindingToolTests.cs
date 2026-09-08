using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Binding;
using AgentCore.Application.Tools.Registry;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// The typed half of <c>kind: binding</c>: the host registers a method, and its signature is the
/// argument schema the model reads.
/// </summary>
/// <remarks>
/// The document writes <c>binds: CreateCase</c> and no <c>parameters:</c>. Everything the model
/// needs to fill the call comes off the method, so the two can never drift apart.
/// </remarks>
public sealed class TypedBindingToolTests
{
    private static readonly ToolConfiguration OpenCase = new()
    {
        Id = "open_case",
        Kind = ToolKind.Binding,
        Binds = "CreateCase",
        Description = "Open a service case for a human agent.",
    };

    // ---------------------------------------------------------------------------------------------
    // The registry.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void ATypedMethodAndAJsonDelegate_AreReadBackApart()
    {
        ToolBindingRegistry registry = new();
        registry.Register("CreateCase", (string summary) => summary);
        registry.Register("CloseCase", (_, _) => ValueTask.FromResult<object?>(null));

        Assert.True(registry.TryGetMethod("CreateCase", out var method));
        Assert.NotNull(method);
        Assert.False(registry.TryGetBinding("CreateCase", out var absentBinding));
        Assert.Null(absentBinding);

        Assert.True(registry.TryGetBinding("CloseCase", out var binding));
        Assert.NotNull(binding);
        Assert.False(registry.TryGetMethod("CloseCase", out var absentMethod));
        Assert.Null(absentMethod);
    }

    [Fact]
    public void ATypedMethod_CountsAgainstTheSameNames()
    {
        ToolBindingRegistry registry = new();
        registry.Register("CreateCase", (string summary) => summary);

        Assert.Equal(1, registry.Count);
        Assert.True(registry.Contains("CreateCase"));
        Assert.Contains("CreateCase", registry.Names);
    }

    [Fact]
    public void TheSameNameTypedThenJson_FailsAtStartup()
    {
        ToolBindingRegistry registry = new();
        registry.Register("CreateCase", (string summary) => summary);

        Assert.Throws<ArgumentException>(
            () => registry.Register("CreateCase", (_, _) => ValueTask.FromResult<object?>(null)));
    }

    // ---------------------------------------------------------------------------------------------
    // The schema the model reads.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheSchemaComesOffTheMethodSignature()
    {
        var tool = await CreateAsync(
            ([Description("What the customer wants.")] string summary, int priority = 3) => $"{summary}/{priority}");

        var properties = tool.JsonSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("summary", out var summaryProperty));
        Assert.True(properties.TryGetProperty("priority", out _));
        Assert.Equal("What the customer wants.", summaryProperty.GetProperty("description").GetString());
    }

    [Fact]
    public async Task AParameterWithADefault_IsNotRequired()
    {
        var tool = await CreateAsync((string summary, int priority = 3) => $"{summary}/{priority}");

        var required = tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("summary", required);
        Assert.DoesNotContain("priority", required);
    }

    [Fact]
    public async Task TheNameAndDescription_StillComeFromTheDocument()
    {
        var tool = await CreateAsync((string summary) => summary);

        Assert.Equal("open_case", tool.Name);
        Assert.Equal("Open a service case for a human agent.", tool.Description);
    }

    // ---------------------------------------------------------------------------------------------
    // Calling.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheModelArgumentsReachTheMethodParameters()
    {
        string? seenSummary = null;
        int seenPriority = 0;
        var tool = await CreateAsync((string summary, int priority) =>
        {
            seenSummary = summary;
            seenPriority = priority;
            return "C-1";
        });

        var result = await tool.InvokeAsync(
            new AIFunctionArguments { ["summary"] = "broken belt", ["priority"] = 1 },
            TestContext.Current.CancellationToken);

        Assert.Equal("broken belt", seenSummary);
        Assert.Equal(1, seenPriority);
        Assert.Equal("C-1", $"{result}");
    }

    /// <summary>Arguments arrive off the wire as <see cref="JsonElement"/>, not as CLR values.</summary>
    [Fact]
    public async Task ArgumentsThatArriveAsJsonElements_BindToo()
    {
        var tool = await CreateAsync((string summary, int priority) => $"{summary}/{priority}");
        using var document = JsonDocument.Parse("""{"summary":"loose bolt","priority":7}""");

        var result = await tool.InvokeAsync(
            new AIFunctionArguments
            {
                ["summary"] = document.RootElement.GetProperty("summary"),
                ["priority"] = document.RootElement.GetProperty("priority"),
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("loose bolt/7", $"{result}");
    }

    [Fact]
    public async Task AnArgumentTheModelLeftOut_TakesTheMethodDefault()
    {
        var tool = await CreateAsync((string summary, int priority = 3) => $"{summary}/{priority}");

        var result = await tool.InvokeAsync(
            new AIFunctionArguments { ["summary"] = "no power" }, TestContext.Current.CancellationToken);

        Assert.Equal("no power/3", $"{result}");
    }

    [Fact]
    public async Task TheCancellationTokenReachesTheMethod()
    {
        var tool = await CreateAsync((string summary, CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return summary;
        });

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await tool.InvokeAsync(new AIFunctionArguments { ["summary"] = "anything" }, cancelled.Token));
    }

    /// <summary>The error policy of section 8.7 keys off <see cref="DeclaredTool"/>, not off the delegate.</summary>
    [Fact]
    public async Task ATypedBinding_IsStillADeclaredTool()
    {
        var tool = await CreateAsync((string summary) => summary);

        Assert.IsAssignableFrom<DeclaredTool>(tool);
    }

    // ---------------------------------------------------------------------------------------------
    // The boot rules.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task ATypedBindingThatAlsoDeclaresParameters_FailsTheBoot()
    {
        ToolBindingRegistry bindings = new();
        bindings.Register("CreateCase", (string summary) => summary);

        var declared = OpenCase with
        {
            Parameters = JsonNode.Parse("""{"type":"object","properties":{"summary":{"type":"string"}}}"""),
        };

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(async () =>
            await new BindingToolSource(bindings).ProvideAsync(
                ContextFor(declared), TestContext.Current.CancellationToken));

        Assert.Contains("parameters:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("open_case", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATypedBinding_BecomesOneRegistration()
    {
        ToolBindingRegistry bindings = new();
        bindings.Register("CreateCase", (string summary) => summary);

        var registrations = await new BindingToolSource(bindings).ProvideAsync(
            ContextFor(OpenCase), TestContext.Current.CancellationToken);

        Assert.Equal("open_case", Assert.Single(registrations).Id);
    }

    private static ToolSourceContext ContextFor(ToolConfiguration tool)
        => new(new AgentCoreConfiguration { ApiVersion = "agentcore/v1", Name = "test", Tools = [tool] });

    /// <summary>Builds the tool the way the boot does: through the source, off the document.</summary>
    private static async Task<AIFunction> CreateAsync(Delegate method)
    {
        ToolBindingRegistry bindings = new();
        bindings.Register("CreateCase", method);

        var registrations = await new BindingToolSource(bindings).ProvideAsync(
            ContextFor(OpenCase), TestContext.Current.CancellationToken);

        return Assert.IsAssignableFrom<AIFunction>(Assert.Single(registrations).Materialise());
    }
}

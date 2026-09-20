using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Registry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// <c>cacheSeconds:</c> on a tool. A repeat of the same arguments must not reach the tool again;
/// different arguments must; a failure must never be served twice.
/// </summary>
public sealed class CachedToolTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static HybridCache NewCache()
    {
        ServiceCollection services = new();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    [Fact]
    public async Task ARepeatOfTheSameArguments_SkipsTheTool_AndReadsTheSameAnswer()
    {
        CountingTool counter = new();
        CachedTool tool = new(counter.Function, NewCache(), TimeSpan.FromMinutes(5));

        var first = await tool.InvokeAsync(new AIFunctionArguments { ["order"] = "A1" }, Token);
        var second = await tool.InvokeAsync(new AIFunctionArguments { ["order"] = "A1" }, Token);

        Assert.Equal(1, counter.Calls);
        Assert.Equal(ToolResultJson.ToNode(first)!.ToJsonString(), ToolResultJson.ToNode(second)!.ToJsonString());
    }

    [Fact]
    public async Task DifferentArguments_ReachTheTool()
    {
        CountingTool counter = new();
        CachedTool tool = new(counter.Function, NewCache(), TimeSpan.FromMinutes(5));

        await tool.InvokeAsync(new AIFunctionArguments { ["order"] = "A1" }, Token);
        await tool.InvokeAsync(new AIFunctionArguments { ["order"] = "B2" }, Token);

        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public async Task TheOrderTheModelWroteTheArgumentsIn_DoesNotChangeTheKey()
    {
        CountingTool counter = new();
        CachedTool tool = new(counter.Function, NewCache(), TimeSpan.FromMinutes(5));

        await tool.InvokeAsync(new AIFunctionArguments { ["order"] = "A1", ["region"] = "us" }, Token);
        await tool.InvokeAsync(new AIFunctionArguments { ["region"] = "us", ["order"] = "A1" }, Token);

        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task TheSideChannel_IsNotPartOfTheKey()
    {
        CountingTool counter = new();
        CachedTool tool = new(counter.Function, NewCache(), TimeSpan.FromMinutes(5));

        await tool.InvokeAsync(new AIFunctionArguments { ["order"] = "A1" }, Token);
        await tool.InvokeAsync(
            WithContext(new AIFunctionArguments { ["order"] = "A1" }, new Dictionary<object, object?> { ["turn"] = 2 }),
            Token);

        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task AFailure_IsNotServedTwice()
    {
        var calls = 0;
        var flaky = AIFunctionFactory.Create(
            (string order) => ++calls == 1 ? ToolErrorResult.Create("lookup", "down") : new JsonObject { ["status"] = "shipped" },
            "lookup",
            "Looks up an order.");
        CachedTool tool = new(flaky, NewCache(), TimeSpan.FromMinutes(5));

        var first = await tool.InvokeAsync(new AIFunctionArguments { ["order"] = "A1" }, Token);
        var second = await tool.InvokeAsync(new AIFunctionArguments { ["order"] = "A1" }, Token);

        Assert.True(ToolErrorResult.IsError(ToolResultJson.ToNode(first)));
        Assert.Equal("shipped", ToolResultJson.ToNode(second)!["status"]!.GetValue<string>());
        Assert.Equal(2, calls);
    }

    [Fact]
    public void TheWrapperShowsTheSameToolToTheModel()
    {
        CountingTool counter = new();
        CachedTool tool = new(counter.Function, NewCache(), TimeSpan.FromSeconds(1));

        Assert.Equal(counter.Function.Name, tool.Name);
        Assert.Equal(counter.Function.Description, tool.Description);
        Assert.Equal(counter.Function.JsonSchema.GetRawText(), tool.JsonSchema.GetRawText());
    }

    [Fact]
    public void ALifetimeOfNothing_IsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new CachedTool(new CountingTool().Function, NewCache(), TimeSpan.Zero));

    // ---------------------------------------------------------------------------------------------
    // The registry is what applies the cache, from the declaration and the host's cache.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task ADeclarationWithCacheSeconds_ResolvesToACachedTool()
    {
        CountingTool counter = new();
        var registry = await ToolRegistryBuilder.BuildAsync(
            [new StubSource(new ToolRegistration("lookup", "d", () => counter.Function))],
            new ToolSourceContext(Documents.With(new ToolConfiguration { Id = "lookup", Kind = ToolKind.Binding, Binds = "host.lookup", CacheSeconds = 60 })) { Cache = NewCache() },
            Token);

        var tool = Assert.IsType<CachedTool>(registry.Resolve("lookup"));
        await tool.InvokeAsync(new AIFunctionArguments { ["order"] = "A1" }, Token);
        await tool.InvokeAsync(new AIFunctionArguments { ["order"] = "A1" }, Token);

        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task ADeclarationWithCacheSeconds_ButAHostWithNoCache_ResolvesToTheToolItself()
    {
        var inner = new CountingTool().Function;
        var registry = await ToolRegistryBuilder.BuildAsync(
            [new StubSource(new ToolRegistration("lookup", "d", () => inner))],
            new ToolSourceContext(Documents.With(new ToolConfiguration { Id = "lookup", Kind = ToolKind.Binding, Binds = "host.lookup", CacheSeconds = 60 })),
            Token);

        Assert.Same(inner, registry.Resolve("lookup"));
    }

    [Fact]
    public async Task ADeclarationWithNoCacheSeconds_ResolvesToTheToolItself()
    {
        var inner = new CountingTool().Function;
        var registry = await ToolRegistryBuilder.BuildAsync(
            [new StubSource(new ToolRegistration("lookup", "d", () => inner))],
            new ToolSourceContext(Documents.With(new ToolConfiguration { Id = "lookup", Kind = ToolKind.Binding, Binds = "host.lookup" })) { Cache = NewCache() },
            Token);

        Assert.Same(inner, registry.Resolve("lookup"));
    }

    private static AIFunctionArguments WithContext(AIFunctionArguments arguments, IDictionary<object, object?> context)
    {
        arguments.Context = context;
        return arguments;
    }

    /// <summary>A tool that answers with the arguments it saw, and counts how often it ran.</summary>
    private sealed class CountingTool
    {
        public int Calls { get; private set; }

        public AIFunction Function { get; }

        public CountingTool()
            => Function = AIFunctionFactory.Create(
                (string order, string? region = null) =>
                {
                    Calls++;
                    return new JsonObject { ["order"] = order, ["region"] = region, ["call"] = Calls };
                },
                "lookup",
                "Looks up an order.");
    }

    private sealed class StubSource(params ToolRegistration[] registrations) : Application.Ports.IToolSource
    {
        public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
            ToolSourceContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(registrations);
    }

    private static class Documents
    {
        public static AgentCoreConfiguration With(params ToolConfiguration[] tools)
            => new() { ApiVersion = "agentcore/v1", Tools = tools, Agents = new AgentsConfiguration { Items = [] }, Entries = new Dictionary<string, EntryConfiguration>() };
    }
}

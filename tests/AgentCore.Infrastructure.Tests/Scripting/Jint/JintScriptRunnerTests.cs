using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Scripting;
using AgentCore.Infrastructure.Scripting.Jint;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Scripting.Jint;

/// <summary>
/// <see cref="JintScriptRunner"/>: what a model-written script can and cannot do. The expected
/// values are ECMAScript, not this code.
/// </summary>
public sealed class JintScriptRunnerTests
{
    private static readonly JsonNode Orders = JsonNode.Parse("""
        [ { "id": "SO-1", "total": 10.5, "date": "2026-03-01" },
          { "id": "SO-2", "total": 20,   "date": "2026-03-09" },
          { "id": "SO-3", "total": 30,   "date": "2026-04-02" } ]
        """)!;

    [Fact]
    public async Task ABody_ThatReturns_HandsBackWhatItReturnedAsJson()
    {
        var result = await Run("return { n: data.length, sum: data.reduce(function (a, o) { return a + o.total; }, 0) };");

        Assert.Null(result.Error);
        Assert.Equal("""{"n":3,"sum":60.5}""", result.Value!.ToJsonString());
    }

    [Fact]
    public async Task ABareArrowFunction_IsCalledWithTheData()
    {
        var result = await Run("(data) => ({ first: data[0].id })");

        Assert.Null(result.Error);
        Assert.Equal("""{"first":"SO-1"}""", result.Value!.ToJsonString());
    }

    [Fact]
    public async Task ANamedFunctionDeclaration_StillReadsAsABody()
    {
        // A body that starts with a helper function is a body, not an expression to call.
        var result = await Run("function name(o) { return o.id; }\nreturn data.map(name);");

        Assert.Null(result.Error);
        Assert.Equal("""["SO-1","SO-2","SO-3"]""", result.Value!.ToJsonString());
    }

    [Fact]
    public async Task AMarkdownFence_IsIgnored()
    {
        var result = await Run("```js\nreturn data.length;\n```");

        Assert.Null(result.Error);
        Assert.Equal("3", result.Value!.ToJsonString());
    }

    [Fact]
    public async Task ACallToTheEmitFunction_CountsAsReturning()
    {
        var result = await Run("present({ ids: data.map(function (o) { return o.id; }) });", emit: "present");

        Assert.Null(result.Error);
        Assert.Equal("""{"ids":["SO-1","SO-2","SO-3"]}""", result.Value!.ToJsonString());
    }

    [Fact]
    public async Task RedeclaringData_ShadowsItInsteadOfFailing()
    {
        // Small models write `const data = [...]` at the top of their body. That shadows the
        // outer binding in ECMAScript, so it must run, not clash.
        var result = await Run("const data = [1, 2]; return data.length;");

        Assert.Null(result.Error);
        Assert.Equal("2", result.Value!.ToJsonString());
    }

    [Fact]
    public async Task ABody_ThatReturnsNothing_HandsBackNoValueAndNoError()
    {
        var result = await Run("var x = 1;");

        Assert.Null(result.Error);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task AThrow_ComesBackAsAnErrorNamingTheMessage()
    {
        var result = await Run("throw new Error('no such column');");

        Assert.Null(result.Value);
        Assert.Contains("no such column", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASyntaxError_ComesBackAsAnError()
    {
        var result = await Run("return { $type: 'Card', ;");

        Assert.Null(result.Value);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task AnEndlessLoop_IsStoppedInsideTheBudget()
    {
        var clock = Stopwatch.StartNew();

        var result = await Run("while (true) { }");

        Assert.NotNull(result.Error);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task ARunawayAllocation_IsStopped()
    {
        var result = await Run("var a = []; while (true) { a.push(new Array(100000).fill('x')); }");

        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task TheHost_IsOutOfReach()
    {
        var result = await Run("return [typeof require, typeof fetch, typeof process, typeof System, typeof importScripts];");

        Assert.Null(result.Error);
        Assert.Equal("""["undefined","undefined","undefined","undefined","undefined"]""", result.Value!.ToJsonString());
    }

    [Fact]
    public async Task ADateOnlyString_ReadsBackItsOwnMonth_WhereverTheHostIs()
    {
        // ECMAScript parses "2026-03-01" as UTC midnight. On a host west of Greenwich a local-time
        // getMonth() answers February; the runner pins the engine to UTC so it answers March.
        var result = await Run("return new Date(data[0].date).getMonth();");

        Assert.Null(result.Error);
        Assert.Equal("2", result.Value!.ToJsonString());
    }

    [Fact]
    public async Task TheCallersCancellation_Throws()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new JintScriptRunner().RunAsync(new ScriptRequest("return 1;", Orders), cancelled.Token));
    }

    private static async Task<ScriptResult> Run(string code, string? emit = null)
        => await new JintScriptRunner().RunAsync(
            new ScriptRequest(code, Orders) { Emit = emit }, TestContext.Current.CancellationToken);
}

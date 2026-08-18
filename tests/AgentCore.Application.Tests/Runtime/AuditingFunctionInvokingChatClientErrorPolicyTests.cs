using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Tools.Fakes;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// Task 7a: the tool error policy moved out of <see cref="DeclaredTool"/> and into
/// <see cref="AuditingFunctionInvokingChatClient.InvokeFunctionAsync"/>, the framework's single choke
/// point for every tool call. These tests pin the caller-observable behaviour that move must not
/// change, and prove the reason for the move: a plain <c>AIFunctionFactory.Create(...)</c> tool, which
/// is not a <see cref="DeclaredTool"/> at all, now gets identical treatment.
/// </summary>
/// <remarks>
/// Every test drives <see cref="AuditingFunctionInvokingChatClient"/> directly against a fake inner
/// <see cref="IChatClient"/>, with no YAML document and no <c>CallSession</c>, so a failure here
/// isolates the seam itself rather than the turn loop built on top of it.
/// </remarks>
public sealed class AuditingFunctionInvokingChatClientErrorPolicyTests
{
    private static readonly ToolConfiguration LookupOrder = new()
    {
        Id = "lookup_order",
        Kind = ToolKind.Binding,
        Binds = "LookupOrder",
        Description = "Read one order by its identifier.",
    };

    // ---------------------------------------------------------------------------------------
    // 3. A fault the model CAN answer still becomes a ToolErrorResult the model reads.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task AFaultTheModelCanAnswer_BecomesTheErrorResultTheModelReads()
    {
        var tool = new ThrowingDeclaredTool(LookupOrder, new InvalidOperationException("the order is already closed."));

        var result = await RunSingleRoundAsync(tool, TestContext.Current.CancellationToken);

        Assert.True(ToolErrorResult.IsError(result));
        Assert.Equal("lookup_order", result[ToolErrorResult.ToolProperty]!.GetValue<string>());
        Assert.Contains(
            "the order is already closed.",
            result[ToolErrorResult.MessageProperty]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // 4. Reporting must not change: an answerable fault never leaves the tool as an exception,
    // so it must never reach ToolFailureScope.Report.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task AFaultTheModelCanAnswer_IsNeverReported()
    {
        List<object> reported = [];
        using var scope = ToolFailureScope.Enter(failure => reported.Add(failure));

        var tool = new ThrowingDeclaredTool(LookupOrder, new InvalidOperationException("the order is already closed."));
        await RunSingleRoundAsync(tool, TestContext.Current.CancellationToken);

        Assert.Empty(reported);
    }

    // ---------------------------------------------------------------------------------------
    // 2. A fault the model CANNOT answer still propagates, so the framework's own consecutive-
    // error budget (MaximumConsecutiveErrorsPerRequest = 3) counts it and the 4th round throws.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task AFaultTheModelCannotAnswer_PropagatesAndSpendsTheConsecutiveErrorBudget()
    {
        var failure = new TimeoutException("the endpoint did not answer.");
        var tool = new ThrowingDeclaredTool(LookupOrder, failure);

        var thrown = await RunUntilTheBudgetThrowsAsync(tool, TestContext.Current.CancellationToken);

        // The very exception, and not a copy: the framework rethrows it by ExceptionDispatchInfo
        // when the budget runs out.
        Assert.Same(failure, thrown);
    }

    // ---------------------------------------------------------------------------------------
    // 5. Stacks are preserved: the middleware uses an exception FILTER for the one case that must
    // never be caught (caller cancellation) and a bare `throw;` for the propagating case, so the
    // stack a beyond-the-model fault carries out still names the tool body that threw it.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task AFaultTheModelCannotAnswer_KeepsItsOriginalStack()
    {
        var failure = new TimeoutException("the endpoint did not answer.");
        var tool = new ThrowingDeclaredTool(LookupOrder, failure);

        var thrown = await RunUntilTheBudgetThrowsAsync(tool, TestContext.Current.CancellationToken);

        Assert.NotNull(thrown.StackTrace);
        Assert.Contains(nameof(ThrowingDeclaredTool), thrown.StackTrace, StringComparison.Ordinal);
        Assert.Contains("CallAsync", thrown.StackTrace, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // 4 (other half). Reporting must not change: a propagating fault is still reported once for
    // every round it spends of the budget, exactly as it is today.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task AFaultTheModelCannotAnswer_IsReportedOnceForEveryPropagatingRound()
    {
        List<object> reported = [];
        using var scope = ToolFailureScope.Enter(failure => reported.Add(failure));

        var failure = new TimeoutException("the endpoint did not answer.");
        var tool = new ThrowingDeclaredTool(LookupOrder, failure);
        await RunUntilTheBudgetThrowsAsync(tool, TestContext.Current.CancellationToken);

        // MaximumConsecutiveErrorsPerRequest is 3, so the 4th round is the one that spends the
        // budget, and all four reached this middleware and were reported before they propagated.
        Assert.Equal(4, reported.Count);
    }

    // ---------------------------------------------------------------------------------------
    // 1. Caller cancellation passes through untouched. The TOKEN decides, never the exception
    // type, and it is never reported.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ACallerThatHungUp_PassesTheCancellationThroughUnreported()
    {
        List<object> reported = [];
        using var scope = ToolFailureScope.Enter(failure => reported.Add(failure));
        using CancellationTokenSource source = new();

        // The tool cancels the very token the call was made with and then throws, exactly as a
        // caller hanging up looks from inside a tool body: the token and the exception type agree.
        var tool = new CancelingDeclaredTool(LookupOrder, source);
        LoopingToolCallingChatClient inner = new();
        using AuditingFunctionInvokingChatClient client = new(inner);
        ChatOptions options = new() { Tools = [tool] };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "where is my order")],
                options,
                source.Token));

        Assert.Empty(reported);
    }

    // ---------------------------------------------------------------------------------------
    // THE UNLOCK: a plain AIFunctionFactory.Create(...) tool is not a DeclaredTool at all, so
    // before Task 7a moved the policy into this middleware, nothing classified its faults — see
    // ThrowingToolFactory in RuntimeFakes.cs, which throws straight at the framework today. After
    // the move it gets the identical answerable-fault treatment a DeclaredTool gets.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task APlainAIFunctionFactoryTool_GetsTheSameErrorResultAsADeclaredTool()
    {
        Func<string> body = () => throw new InvalidOperationException("the order is already closed.");
        AIFunction tool = AIFunctionFactory.Create(body, "lookup_order", "Read one order by its identifier.");

        var result = await RunSingleRoundAsync(tool, TestContext.Current.CancellationToken);

        Assert.True(ToolErrorResult.IsError(result));
        Assert.Equal("lookup_order", result[ToolErrorResult.ToolProperty]!.GetValue<string>());
        Assert.Contains(
            "the order is already closed.",
            result[ToolErrorResult.MessageProperty]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Coverage restored after review: BuiltinToolTests and BindingToolTests now only prove that
    // a REAL Builtin / Binding tool lets its adapter's fault propagate — the classification and
    // conversion into ToolErrorResult that used to be proven there is proven here instead, against
    // the real factory-built tool and the real middleware, not a synthetic fake.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ARealBuiltinTool_GetsTheSameErrorResultThroughTheRealMiddleware()
    {
        MapKnowledgePort knowledge = new() { Failure = new InvalidOperationException("the store is down") };
        ToolConfiguration search = new()
        {
            Id = "search_chunks",
            Kind = ToolKind.Builtin,
            Uses = BuiltinToolNames.KnowledgeSearch,
        };
        var tool = Assert.IsAssignableFrom<AIFunction>(new BuiltinToolFactory(knowledge, null).Create(search));

        // A query the tool's own validation accepts, so the call actually reaches the adapter that
        // throws rather than stopping at the "no query" check first.
        var result = await RunSingleRoundAsync(
            tool,
            TestContext.Current.CancellationToken,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "refund" });

        Assert.True(ToolErrorResult.IsError(result));
        Assert.Equal("search_chunks", result[ToolErrorResult.ToolProperty]!.GetValue<string>());
        Assert.Contains(
            "the store is down",
            result[ToolErrorResult.MessageProperty]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARealBindingTool_GetsTheSameErrorResultThroughTheRealMiddleware()
    {
        ToolBindingRegistry registry = new();
        registry.Register("CreateCase", (arguments, cancellationToken)
            => throw new InvalidOperationException("the case system is down"));
        ToolConfiguration createCase = new()
        {
            Id = "create_case",
            Kind = ToolKind.Binding,
            Binds = "CreateCase",
            Description = "Open a service case for a human agent.",
        };
        var tool = Assert.IsAssignableFrom<AIFunction>(new BindingToolFactory(registry).Create(createCase));

        var result = await RunSingleRoundAsync(tool, TestContext.Current.CancellationToken);

        Assert.True(ToolErrorResult.IsError(result));
        Assert.Equal("create_case", result[ToolErrorResult.ToolProperty]!.GetValue<string>());
        Assert.Contains(
            "the case system is down",
            result[ToolErrorResult.MessageProperty]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    /// <summary>Runs one request/response round and returns the JSON the tool result carried.</summary>
    /// <param name="tool">The tool the fake model calls.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <param name="arguments">
    /// The arguments the fake model fills, or <see langword="null"/> for none — enough for a tool
    /// that validates its own arguments before it ever reaches its adapter.
    /// </param>
    /// <remarks>
    /// <see cref="ToolCallingChatClient"/> calls the one offered tool once and then answers with
    /// text, which keeps the round finite regardless of what the tool call returns.
    /// </remarks>
    private static async Task<JsonObject> RunSingleRoundAsync(
        AIFunction tool,
        CancellationToken cancellationToken,
        Dictionary<string, object?>? arguments = null)
    {
        ToolCallingChatClient inner = new("the loop continues.", arguments);
        using AuditingFunctionInvokingChatClient client = new(inner);
        ChatOptions options = new() { Tools = [tool] };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "where is my order")],
            options,
            cancellationToken);

        var raw = Assert.Single(inner.ToolResults);
        return Assert.IsType<JsonObject>(JsonNode.Parse(raw));
    }

    /// <summary>
    /// Runs rounds until the framework's own <c>MaximumConsecutiveErrorsPerRequest</c> budget throws,
    /// and returns what it threw.
    /// </summary>
    /// <remarks>
    /// <see cref="LoopingToolCallingChatClient"/> never stops calling the tool, so a fault that
    /// propagates on every round spends the budget on the fourth.
    /// </remarks>
    private static async Task<Exception> RunUntilTheBudgetThrowsAsync(AIFunction tool, CancellationToken cancellationToken)
    {
        LoopingToolCallingChatClient inner = new();
        using AuditingFunctionInvokingChatClient client = new(inner);
        ChatOptions options = new() { Tools = [tool] };

        return await Assert.ThrowsAnyAsync<Exception>(() =>
            client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "where is my order")],
                options,
                cancellationToken));
    }

    /// <summary>A <see cref="DeclaredTool"/> whose body throws whatever the test hands it.</summary>
    private sealed class ThrowingDeclaredTool : DeclaredTool
    {
        private readonly Exception _failure;

        public ThrowingDeclaredTool(ToolConfiguration tool, Exception failure)
            : base(tool) => _failure = failure;

        protected override ValueTask<object?> CallAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) => throw _failure;
    }

    /// <summary>A <see cref="DeclaredTool"/> whose body cancels the caller's own token and then throws.</summary>
    private sealed class CancelingDeclaredTool : DeclaredTool
    {
        private readonly CancellationTokenSource _source;

        public CancelingDeclaredTool(ToolConfiguration tool, CancellationTokenSource source)
            : base(tool) => _source = source;

        protected override ValueTask<object?> CallAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            _source.Cancel();
            throw new OperationCanceledException();
        }
    }
}

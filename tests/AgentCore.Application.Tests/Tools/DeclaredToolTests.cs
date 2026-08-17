using System.Net.Sockets;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// The one rule every tool kind shares: a fault the model can answer becomes a result it reads, and a
/// fault the model cannot answer is rethrown so the framework's budget can end the turn.
/// </summary>
/// <remarks>
/// <para>
/// Section 8.7 says a tool returns an error result and does not throw, and that stays true for the
/// failure it was written about: the endpoint said no, the argument was wrong, the record does not
/// exist. The model reads the answer and tries something else, and that is the design every major
/// agent framework and the MCP specification converged on.
/// </para>
/// <para>
/// It was never true for the other half. A catch-all made "the caller typed a bad order number" and
/// "the database is unreachable" the same fact, so <c>MaximumConsecutiveErrorsPerRequest</c> could
/// never fire for a transport that was simply down and the model spent the whole turn retrying a
/// socket. These tests pin the split.
/// </para>
/// </remarks>
public sealed class DeclaredToolTests
{
    private static readonly ToolConfiguration LookupOrder = new()
    {
        Id = "lookup_order",
        Kind = ToolKind.Binding,
        Binds = "LookupOrder",
        Description = "Read one order by its identifier.",
    };

    /// <summary>Every fault the model may be able to answer. It becomes a result, exactly as before.</summary>
    public static TheoryData<Exception> FaultsTheModelMayAnswer =>
    [
        new InvalidOperationException("the order is already closed."),
        new ArgumentException("orderId is not a number."),
        new FormatException("the date is not a date."),
        new KeyNotFoundException("no such order."),
        new NotSupportedException("this endpoint does not take a range."),
    ];

    /// <summary>Every fault the model cannot answer. It is rethrown, and the budget counts it.</summary>
    public static TheoryData<Exception> FaultsTheModelCannotAnswer =>
    [
        new HttpRequestException("no such host"),
        new SocketException(),
        new IOException("the disk went away."),
        new TimeoutException("the endpoint did not answer."),
        new UnauthorizedAccessException("the token was rejected."),
        new TaskCanceledException("The request timed out.", new TimeoutException()),
    ];

    [Theory]
    [MemberData(nameof(FaultsTheModelMayAnswer))]
    public async Task AFaultTheModelMayAnswer_BecomesTheErrorResultTheModelReads(Exception failure)
    {
        var result = await new ThrowingTool(failure).InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        // The converged design, and it must not regress: the loop continues and the model recovers.
        Assert.True(ToolErrorResult.IsError(result as System.Text.Json.Nodes.JsonNode));
    }

    [Theory]
    [MemberData(nameof(FaultsTheModelCannotAnswer))]
    public async Task AFaultTheModelCannotAnswer_IsRethrownSoTheBudgetCountsIt(Exception failure)
    {
        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            async () => await new ThrowingTool(failure).InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken));

        // The very exception, and not a copy: the framework rethrows it by ExceptionDispatchInfo when
        // the budget runs out, and a stack trace that started here is the only thing that names the
        // tool in the log.
        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task ACallerThatHungUp_StillPassesTheCancellationThrough()
    {
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        // Nobody reads this result, and swallowing it would keep a dead call running.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new ThrowingTool(new OperationCanceledException()).InvokeAsync(
                new AIFunctionArguments(),
                source.Token));
    }

    [Fact]
    public async Task ATimeoutThatIsNotTheCallersCancellation_IsRethrownAndNotSwallowed()
    {
        // HttpClient reports its own deadline as a TaskCanceledException with a TimeoutException
        // inside it, on a token the caller never cancelled. It reads like a cancellation and it is a
        // dead endpoint, so the token decides and not the type.
        TaskCanceledException timeout = new("The request timed out.", new TimeoutException());

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            async () => await new ThrowingTool(timeout).InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken));

        Assert.Same(timeout, thrown);
    }

    [Fact]
    public async Task AToolKindThatRefinesTheSplit_IsObeyed()
    {
        // The split is one virtual method, so a tool kind whose vendor SDK spells a transport fault
        // its own way narrows or widens it without a second catch block in every tool body.
        var result = await new ForgivingTool(new HttpRequestException("no such host")).InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        Assert.True(ToolErrorResult.IsError(result as System.Text.Json.Nodes.JsonNode));
    }

    /// <summary>A tool whose body throws whatever the test hands it.</summary>
    private sealed class ThrowingTool : DeclaredTool
    {
        private readonly Exception _failure;

        public ThrowingTool(Exception failure)
            : base(LookupOrder) => _failure = failure;

        protected override ValueTask<object?> CallAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) => throw _failure;
    }

    /// <summary>A tool kind that has decided the model can answer everything.</summary>
    private sealed class ForgivingTool : DeclaredTool
    {
        private readonly Exception _failure;

        public ForgivingTool(Exception failure)
            : base(LookupOrder) => _failure = failure;

        protected override bool IsBeyondTheModel(Exception failure) => false;

        protected override ValueTask<object?> CallAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) => throw _failure;
    }
}

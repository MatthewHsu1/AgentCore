using System.Net.Sockets;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// <see cref="DeclaredTool.InvokeCoreAsync"/> no longer applies an error policy at all: it runs
/// <see cref="DeclaredTool.CallAsync"/> and returns or throws exactly what that returned or threw.
/// </summary>
/// <remarks>
/// <para>
/// Task 7a moved the split between a fault the model can answer and a fault beyond it out of this
/// base class and into <c>AuditingFunctionInvokingChatClient.InvokeFunctionAsync</c>, the framework's
/// single choke point for every tool call — see
/// <c>AgentCore.Application.Tests.Runtime.AuditingFunctionInvokingChatClientErrorPolicyTests</c> for
/// the classification itself and the behaviour it pins.
/// </para>
/// <para>
/// What these tests pin instead is narrower and still real: a <see cref="DeclaredTool"/> body may
/// still answer a fault itself by returning <see cref="ToolErrorResult"/> directly without throwing —
/// a builtin tool definition does exactly that for an argument it already validated — and everything
/// this base class does NOT catch keeps its original stack, cancellation included, all the way out to
/// whatever calls <see cref="AIFunction.InvokeAsync"/>.
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

    /// <summary>
    /// A representative fault of each kind the old split once told apart. Both propagate identically
    /// now, because <see cref="DeclaredTool"/> no longer classifies anything.
    /// </summary>
    public static TheoryData<Exception> RepresentativeFaults =>
    [
        new InvalidOperationException("the order is already closed."),
        new ArgumentException("orderId is not a number."),
        new FormatException("the date is not a date."),
        new KeyNotFoundException("no such order."),
        new NotSupportedException("this endpoint does not take a range."),
        new HttpRequestException("no such host"),
        new SocketException(),
        new IOException("the disk went away."),
        new TimeoutException("the endpoint did not answer."),
        new UnauthorizedAccessException("the token was rejected."),
        new TaskCanceledException("The request timed out.", new TimeoutException()),
    ];

    [Theory]
    [MemberData(nameof(RepresentativeFaults))]
    public async Task AnyFaultTheBodyThrows_PropagatesUnfiltered(Exception failure)
    {
        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            async () => await new ThrowingTool(failure).InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken));

        // The very exception, and not a copy: InvokeCoreAsync no longer catches, so this is a plain
        // await of CallAsync and nothing rewraps the fault. Whether the model could have answered it
        // is now decided one layer up, by AuditingFunctionInvokingChatClient.
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
        // dead endpoint. Nothing here tests the token any more, but the result is the same: it is
        // never swallowed.
        TaskCanceledException timeout = new("The request timed out.", new TimeoutException());

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            async () => await new ThrowingTool(timeout).InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken));

        Assert.Same(timeout, thrown);
    }

    [Fact]
    public async Task ABodyThatAnswersItself_StillReturnsTheErrorResultDirectly()
    {
        // The one half of the old rule that still lives here: a tool body may choose to answer a
        // fault itself, without throwing at all, exactly as a builtin tool definition does today.
        var result = await new SelfAnsweringTool().InvokeAsync(
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

    /// <summary>A tool whose body returns its own error result rather than throwing.</summary>
    private sealed class SelfAnsweringTool : DeclaredTool
    {
        public SelfAnsweringTool()
            : base(LookupOrder)
        {
        }

        protected override ValueTask<object?> CallAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<object?>(Failed("the order is already closed."));
    }
}

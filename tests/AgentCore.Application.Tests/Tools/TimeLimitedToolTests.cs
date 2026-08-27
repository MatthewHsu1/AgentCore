using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Registry;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// The deadline any tool kind can carry.
/// </summary>
/// <remarks>
/// A tool the model calls on a live telephone call can otherwise take as long as the far side feels
/// like taking. The deadline wraps a tool rather than living inside one, so one implementation and
/// one message cover every kind rather than each kind growing its own.
/// </remarks>
public sealed class TimeLimitedToolTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AToolThatAnswersInTime_IsUntouched()
    {
        TimeLimitedTool tool = new(Answering("done"), TimeSpan.FromSeconds(30));

        var result = await tool.InvokeAsync(new AIFunctionArguments(), Token);

        Assert.Equal("done", Assert.IsType<JsonElement>(result).GetString());
    }

    [Fact]
    public async Task AToolThatRunsLong_ReturnsAnErrorResult_AndDoesNotThrow()
    {
        TimeLimitedTool tool = new(Hanging(), TimeSpan.FromMilliseconds(50));

        var result = await tool.InvokeAsync(new AIFunctionArguments(), Token);

        // Section 8.7: the model reads the result and decides what to say next. An exception would
        // end the turn while a caller is on the line.
        var error = Assert.IsType<JsonObject>(result);
        Assert.True(ToolErrorResult.IsError(error));
        Assert.Equal("slow", error[ToolErrorResult.ToolProperty]!.GetValue<string>());
        Assert.Contains("0.05s", error[ToolErrorResult.MessageProperty]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The turn being cancelled is the caller's own token and belongs to the caller. Only this
    /// wrapper's own deadline becomes a result.
    /// </summary>
    [Fact]
    public async Task TheTurnBeingCancelled_StillThrows_AndIsNotReportedAsATimeout()
    {
        TimeLimitedTool tool = new(Hanging(), TimeSpan.FromMinutes(5));
        using CancellationTokenSource turn = new();
        await turn.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await tool.InvokeAsync(new AIFunctionArguments(), turn.Token));
    }

    [Fact]
    public void TheWrapperShowsTheSameToolToTheModel()
    {
        var inner = Answering("x");
        TimeLimitedTool tool = new(inner, TimeSpan.FromSeconds(1));

        Assert.Equal(inner.Name, tool.Name);
        Assert.Equal(inner.Description, tool.Description);
        Assert.Equal(inner.JsonSchema.GetRawText(), tool.JsonSchema.GetRawText());
    }

    [Fact]
    public void ADeadlineOfNothing_IsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new TimeLimitedTool(Answering("x"), TimeSpan.Zero));

    // ---------------------------------------------------------------------------------------------
    // The registry is what applies the deadline, so every kind gets the same one.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task ARegistrationThatNamesATimeout_ResolvesToALimitedTool()
    {
        var registry = await ToolRegistryBuilder.BuildAsync(
            [new StubSource(new ToolRegistration("slow", "d", Hanging, TimeSpan.FromMilliseconds(50)))],
            new ToolSourceContext(Documents.Empty),
            Token);

        var result = await ((AIFunction)registry.Resolve("slow")).InvokeAsync(new AIFunctionArguments(), Token);

        Assert.True(ToolErrorResult.IsError(Assert.IsType<JsonObject>(result)));
    }

    /// <summary>
    /// A source that names no timeout gets no wrapper: a <c>kind: agent</c> tool runs a whole inner
    /// agent loop, and a default deadline over that would cut off work that is going fine.
    /// </summary>
    [Fact]
    public async Task ARegistrationThatNamesNoTimeout_ResolvesToTheToolItself()
    {
        var inner = Answering("plain");
        var registry = await ToolRegistryBuilder.BuildAsync(
            [new StubSource(new ToolRegistration("plain", "d", () => inner))],
            new ToolSourceContext(Documents.Empty),
            Token);

        Assert.Same(inner, registry.Resolve("plain"));
    }

    private static AIFunction Answering(string answer)
        => AIFunctionFactory.Create(() => answer, "quick", "Answers at once.");

    private static AIFunction Hanging()
        => AIFunctionFactory.Create(
            async (CancellationToken ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return "never";
            },
            "slow",
            "Never answers.");

    private sealed class StubSource(params ToolRegistration[] registrations) : Application.Ports.IToolSource
    {
        public ValueTask<IReadOnlyList<ToolRegistration>> ProvideAsync(
            ToolSourceContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ToolRegistration>>(registrations);
    }

    private static class Documents
    {
        public static Application.Configuration.Schema.AgentCoreConfiguration Empty { get; }
            = new() { ApiVersion = "agentcore/v1", Name = "tools" };
    }
}

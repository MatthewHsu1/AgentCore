using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools;

/// <summary>
/// One tool, with a deadline on every call.
/// </summary>
public sealed class TimeLimitedTool : DelegatingAIFunction
{
    private readonly TimeSpan _limit;

    /// <summary>Creates the wrapper.</summary>
    /// <param name="inner">The tool being limited.</param>
    /// <param name="limit">How long one call may take. It must be positive.</param>
    public TimeLimitedTool(AIFunction inner, TimeSpan limit)
        : base(inner)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, TimeSpan.Zero);

        _limit = limit;
    }

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_limit);

        try
        {
            return await base.InvokeCoreAsync(arguments, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The turn itself being cancelled is not this: that token is the caller's, and it is left
            // to propagate. Only this wrapper's own deadline becomes a result.
            return Failed();
        }
    }

    private JsonObject Failed()
        => ToolErrorResult.Create(
            Name,
            $"the tool did not answer within {_limit.TotalSeconds:0.###}s and was given up on.");
}

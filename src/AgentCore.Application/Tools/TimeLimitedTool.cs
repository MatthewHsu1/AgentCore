using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools;

/// <summary>
/// One tool, with a deadline on every call.
/// </summary>
public sealed class TimeLimitedTool : AIFunction
{
    private readonly AIFunction _inner;

    private readonly TimeSpan _limit;

    /// <summary>Creates the wrapper.</summary>
    /// <param name="inner">The tool being limited.</param>
    /// <param name="limit">How long one call may take. It must be positive.</param>
    public TimeLimitedTool(AIFunction inner, TimeSpan limit)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, TimeSpan.Zero);

        _inner = inner;
        _limit = limit;
    }

    /// <inheritdoc />
    public override string Name => _inner.Name;

    /// <inheritdoc />
    public override string Description => _inner.Description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => _inner.JsonSchema;

    /// <inheritdoc />
    public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;

    /// <inheritdoc />
    public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_limit);

        try
        {
            return await _inner.InvokeAsync(arguments, deadline.Token).ConfigureAwait(false);
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
            _inner.Name,
            $"the tool did not answer within {_limit.TotalSeconds:0.###}s and was given up on.");
}

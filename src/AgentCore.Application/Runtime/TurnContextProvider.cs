using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The framework's per-invocation seam, bound to every compiled agent.
/// </summary>
internal sealed class TurnContextProvider : AIContextProvider
{
    /// <inheritdoc />
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new(new AIContext { Instructions = TurnContextScope.For(context.Session)?.Instructions });
    }
}

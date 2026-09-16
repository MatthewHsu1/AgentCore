using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Turn;

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

        var instructions = TurnRegistry.For(context.Session)?.Instructions;

        return new(new AIContext
        {
            Messages = string.IsNullOrEmpty(instructions)
                ? null
                : [new ChatMessage(ChatRole.System, instructions)],
        });
    }
}

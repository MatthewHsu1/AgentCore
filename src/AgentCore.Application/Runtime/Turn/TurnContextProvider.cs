using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Turn
{
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

            TurnInvocation? turn = TurnRegistry.For(context.Session);
            List<ChatMessage> messages = [];
            if (turn?.Instructions is { Length: > 0 } instructions)
            {
                messages.Add(new ChatMessage(ChatRole.System, instructions));
            }

            if (turn?.AddedContext is { IsEmpty: false } added)
            {
                messages.AddRange(added.Select(static note => new ChatMessage(ChatRole.System, note)));
            }

            return new(new AIContext { Messages = messages.Count == 0 ? null : messages });
        }
    }
}

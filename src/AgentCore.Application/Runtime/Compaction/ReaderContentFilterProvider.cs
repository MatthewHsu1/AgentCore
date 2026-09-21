using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime.Compaction
{
    /// <summary>
    /// Removes reader content (<see cref="ReaderContent"/>) from the messages bound for the model.
    /// </summary>
    internal sealed class ReaderContentFilterProvider : AIContextProvider
    {
        /// <inheritdoc />
        protected override ValueTask<AIContext> InvokingCoreAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            AIContext input = context.AIContext;
            return input.Messages is null
                ? new(input)
                : new(new AIContext
                {
                    Instructions = input.Instructions,
                    Messages = ReaderContent.Strip(input.Messages).ToList(),
                    Tools = input.Tools,
                });
        }
    }
}

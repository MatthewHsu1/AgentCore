using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// Answers the first call to the one declared tool at once, and blocks the second call until the
    /// run's own cancellation ends it.
    /// </summary>
    internal sealed class PartiallyAnsweredToolFactory
    {
        /// <summary>Signals once the second call is in flight and blocked.</summary>
        public TaskCompletionSource SecondConversationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AIFunction? Create(ToolConfiguration tool)
        {
            ArgumentNullException.ThrowIfNull(tool);
            return AIFunctionFactory.Create(AnswerAsync, tool.Id, tool.Description ?? tool.Id);
        }

        private async Task<string> AnswerAsync(int index, CancellationToken cancellationToken)
        {
            if (index == 1)
            {
                return "50";
            }

            _ = SecondConversationStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            // Unreachable: the delay above only ever ends in cancellation.
            return string.Empty;
        }
    }
}

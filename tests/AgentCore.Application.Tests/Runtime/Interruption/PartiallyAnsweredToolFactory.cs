using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// Answers the first call to the one declared tool at once, and blocks the second call until
    /// <see cref="ReleaseSecond"/> completes. A cancellation that reaches the second call ends it unanswered.
    /// </summary>
    internal sealed class PartiallyAnsweredToolFactory
    {
        /// <summary>The answer the second call gives once released.</summary>
        public const string SecondAnswer = "70";

        /// <summary>Signals once the second call is in flight and blocked.</summary>
        public TaskCompletionSource SecondConversationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets the blocked second call answer.</summary>
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            await ReleaseSecond.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return SecondAnswer;
        }
    }
}

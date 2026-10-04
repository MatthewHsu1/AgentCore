using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Tools
{
    /// <summary>Holds every call until a given number of calls are in flight at once, then lets them all run.</summary>
    internal sealed class MeetingFunction(AIFunction inner, int parties) : DelegatingAIFunction(inner)
    {
        private readonly TaskCompletionSource _met = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) >= parties)
            {
                _met.TrySetResult();
            }

            await _met.Task.WaitAsync(cancellationToken);
            return await base.InvokeCoreAsync(arguments, cancellationToken);
        }
    }
}

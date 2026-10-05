using Microsoft.Extensions.AI;

namespace AgentCore.TestSupport
{
    /// <summary>
    /// A tool that counts its runs and holds each one until <see cref="Release"/> completes. A run whose token is
    /// cancelled while it waits ends there, completes <see cref="Cancelled"/>, and never counts as finished.
    /// </summary>
    public sealed class GatedTool
    {
        /// <summary>What every finished run returns.</summary>
        public const string Result = """{"price":42}""";

        private int _runs;

        private int _finished;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Runs => Volatile.Read(ref _runs);

        public int Finished => Volatile.Read(ref _finished);

        public AIFunction Create(string name, string description) => AIFunctionFactory.Create(RunAsync, name, description);

        private async Task<string> RunAsync(CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _runs);
            _ = Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ = Cancelled.TrySetResult();
                throw;
            }

            _ = Interlocked.Increment(ref _finished);
            return Result;
        }
    }
}

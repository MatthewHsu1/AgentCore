using AgentCore.AspNetCore.Voice.Threading;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary><see cref="TaskTeardown.CancelAndWaitAsync"/> waits for every task and raises none of their faults.</summary>
    public sealed class TaskTeardownTests
    {
        [Fact(Timeout = 30_000)]
        public async Task CancelAndWaitAsync_SwallowsCancellationFromTheTokenItCancelled()
        {
            using CancellationTokenSource cts = new();
            Task task = Task.Delay(Timeout.Infinite, cts.Token);

            await TaskTeardown.CancelAndWaitAsync(cts, [task]);

            Assert.True(task.IsCanceled);
        }

        // LiveKit utils/aio/utils.py::cancel_and_wait awaits _release_waiter, which never raises the future's error.
        [Fact(Timeout = 30_000)]
        public async Task CancelAndWaitAsync_DoesNotRaiseAFaultThatIsNotCancellation()
        {
            using CancellationTokenSource cts = new();
            Task task = Task.FromException(new InvalidOperationException("boom"));

            await TaskTeardown.CancelAndWaitAsync(cts, [task]);

            Assert.True(task.IsFaulted);
            Assert.True(cts.IsCancellationRequested);
        }

        // LiveKit utils/log.py::log_exceptions logs the error, then raises it again.
        [Fact(Timeout = 30_000)]
        public async Task LogExceptions_LogsAFaultOnce_AndRaisesItToTheOwner()
        {
            InvalidOperationException boom = new("boom");
            List<Exception> logged = [];
            Func<Task> body = TaskTeardown.LogExceptions(() => Task.FromException(boom), logged.Add);

            InvalidOperationException raised = await Assert.ThrowsAsync<InvalidOperationException>(body);

            Assert.Same(boom, raised);
            Assert.Same(boom, Assert.Single(logged));
        }
    }
}

using AgentCore.AspNetCore.Voice.Threading;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>Set/clear/wait races on <see cref="AsyncEvent"/>.</summary>
    public sealed class AsyncEventTests
    {
        [Fact(Timeout = 30_000)]
        public async Task AWaiter_BlocksUntilSetIsCalled()
        {
            AsyncEvent ev = new();
            Task waiting = ev.WaitAsync(TestContext.Current.CancellationToken);

            Assert.False(waiting.IsCompleted);

            ev.Set();

            await waiting;
            Assert.True(ev.IsSet);
        }

        [Fact(Timeout = 30_000)]
        public async Task Clear_ClosesTheGateAgainForTheNextRound()
        {
            AsyncEvent ev = new();
            ev.Set();
            ev.Clear();

            Assert.False(ev.IsSet);

            Task waiting = ev.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(waiting.IsCompleted);

            ev.Set();
            await waiting;
        }

        [Fact(Timeout = 30_000)]
        public async Task AWaiterAlreadyReleasedBySet_IsUnaffectedByALaterClear()
        {
            AsyncEvent ev = new();
            Task released = ev.WaitAsync(TestContext.Current.CancellationToken);

            ev.Set();
            ev.Clear();

            await released;
            Assert.True(released.IsCompletedSuccessfully);
        }

        // asyncio.Event.clear() on an event that is not set changes nothing for its waiters.
        [Fact]
        public void ClearOnAnEventThatIsNotSet_KeepsTheWaiterForTheNextSet()
        {
            AsyncEvent ev = new();

            // No token, so the waiter is the event's own task and completes inside Set.
            Task waiting = ev.WaitAsync(CancellationToken.None);

            ev.Clear();
            ev.Set();

            Assert.True(waiting.IsCompletedSuccessfully);
        }

        [Fact(Timeout = 30_000)]
        public async Task CancellingAWait_DoesNotSetTheEventForOtherWaiters()
        {
            AsyncEvent ev = new();
            using CancellationTokenSource cts = new();
            Task cancelled = ev.WaitAsync(cts.Token);
            Task other = ev.WaitAsync(TestContext.Current.CancellationToken);

            await cts.CancelAsync();
            await Assert.ThrowsAsync<TaskCanceledException>(() => cancelled);

            Assert.False(ev.IsSet);
            Assert.False(other.IsCompleted);

            ev.Set();
            await other;
        }
    }
}

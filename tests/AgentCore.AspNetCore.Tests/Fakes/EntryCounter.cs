using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>Counts the requests that reached the entry gate, and decides nothing.</summary>
    internal sealed class EntryCounter : AgentHook
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override ValueTask BeforeEntryAsync(EntryGate gate, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }
    }
}

using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>An audit store that is slow to write and records what it held when it was closed.</summary>
    internal sealed class ClosingAuditSink : IAuditSinkPort, IAsyncDisposable
    {
        private static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(200);

        private int _written;

        /// <summary>Gets the number of events this store has written.</summary>
        public int Written => Volatile.Read(ref _written);

        /// <summary>Gets whether this store was closed.</summary>
        public bool Closed { get; private set; }

        /// <summary>Gets how many events this store had written by the time it was closed.</summary>
        public int WrittenWhenClosed { get; private set; }

        public async ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            await Task.Delay(WriteDelay, CancellationToken.None);
            _ = Interlocked.Increment(ref _written);
        }

        public ValueTask DisposeAsync()
        {
            Closed = true;
            WrittenWhenClosed = Written;
            return ValueTask.CompletedTask;
        }
    }
}

using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>A sideband a test feeds by hand; it records what the call sent, in order.</summary>
    internal sealed class FakeSideband : ILiveSideband
    {
        private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
        private readonly List<JsonObject> _sent = [];
        private readonly List<(Func<JsonObject, bool> Match, TaskCompletionSource<JsonObject> Seen)> _waiters = [];
        private readonly Lock _reading = new();
        private readonly List<TaskCompletionSource> _drainWaiters = [];
        private int _pushed;
        private int _read;
        private bool _parked;

        public bool Disposed { get; private set; }

        /// <summary>Gets what <see cref="DisposeAsync"/> throws once it closed the socket, or <see langword="null"/> for nothing.</summary>
        public Exception? DisposeFault { get; init; }

        /// <summary>Gets a task every send waits for once it is recorded, or <see langword="null"/> for no wait.</summary>
        public Task? SendsHeldUntil { get; init; }

        /// <summary>Gets how many events the test has pushed so far.</summary>
        public int Pushed
        {
            get
            {
                lock (_reading)
                {
                    return _pushed;
                }
            }
        }

        public IReadOnlyList<JsonObject> Sent
        {
            get
            {
                lock (_sent)
                {
                    return [.. _sent];
                }
            }
        }

        public void Push(IEnumerable<string> events)
        {
            foreach (string liveEvent in events)
            {
                lock (_reading)
                {
                    _pushed++;
                    _ = _inbound.Writer.TryWrite(liveEvent);
                }
            }
        }

        /// <summary>Pushes GPT-Live's acknowledgement of one append the call sent, in the shape a recorded GPT-Live session showed.</summary>
        public void Ack(JsonObject sent)
        {
            JsonObject ack = new()
            {
                ["type"] = "session.commentary.appended",
                ["start_ms"] = 13400,
                ["end_ms"] = 13600,
                ["event_id"] = "event_test_ack",
                ["client_event_id"] = (string?)sent["event_id"],
            };
            Push([ack.ToJsonString()]);
        }

        /// <summary>Closes the socket with no <c>session.closed</c>.</summary>
        public void Complete() => _inbound.Writer.TryComplete();

        /// <summary>Breaks the socket: the next receive after the pushed events throws <paramref name="fault"/>.</summary>
        public void Fail(Exception fault) => _inbound.Writer.TryComplete(fault);

        public Task<JsonObject> WaitForSentAsync(Func<JsonObject, bool> match)
        {
            TaskCompletionSource<JsonObject> seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sent)
            {
                if (_sent.FirstOrDefault(match) is { } already)
                {
                    return Task.FromResult(already);
                }

                _waiters.Add((match, seen));
            }

            return seen.Task;
        }

        /// <summary>
        /// Completes once the call has handled every pushed event and is waiting for the next: the loop asks for a new
        /// message only after it handled the last one.
        /// </summary>
        public Task WaitForDrainAsync()
        {
            lock (_reading)
            {
                if (_parked && _read == _pushed)
                {
                    return Task.CompletedTask;
                }

                TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _drainWaiters.Add(drained);
                return drained.Task;
            }
        }

        public async ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                List<TaskCompletionSource> drained;
                lock (_reading)
                {
                    if (_inbound.Reader.TryRead(out string? liveEvent))
                    {
                        _read++;
                        _parked = false;
                        return liveEvent;
                    }

                    _parked = true;
                    drained = _read == _pushed ? [.. _drainWaiters] : [];
                    _ = _drainWaiters.RemoveAll(drained.Contains);
                }

                drained.ForEach(waiter => waiter.TrySetResult());
                if (!await _inbound.Reader.WaitToReadAsync(cancellationToken))
                {
                    return null;
                }
            }
        }

        public ValueTask SendAsync(string json, CancellationToken cancellationToken)
        {
            JsonObject sent = JsonNode.Parse(json)!.AsObject();
            List<TaskCompletionSource<JsonObject>> matched = [];
            lock (_sent)
            {
                _sent.Add(sent);
                foreach ((Func<JsonObject, bool> match, TaskCompletionSource<JsonObject> seen) in _waiters.Where(waiter => waiter.Match(sent)).ToList())
                {
                    matched.Add(seen);
                    _ = _waiters.Remove((match, seen));
                }
            }

            matched.ForEach(seen => seen.TrySetResult(sent));
            return SendsHeldUntil is { } held ? new ValueTask(held.WaitAsync(cancellationToken)) : default;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Complete();
            return DisposeFault is { } fault ? ValueTask.FromException(fault) : default;
        }
    }
}

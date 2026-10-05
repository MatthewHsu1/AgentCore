using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Notices;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>Records every notice it is handed, in delivery order; may hold each delivery until released.</summary>
    internal sealed class Recorder(TimeSpan? timeout = null) : AgentHook
    {
        private readonly Lock _gate = new();
        private readonly List<HookNotice> _seen = [];
        private readonly List<int> _threads = [];

        public TaskCompletionSource? Hold { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<HookNotice> Seen
        {
            get
            {
                lock (_gate)
                {
                    return [.. _seen];
                }
            }
        }

        public IReadOnlyList<int> Threads
        {
            get
            {
                lock (_gate)
                {
                    return [.. _threads];
                }
            }
        }

        public override TimeSpan? NoticeTimeout => timeout;

        public override ValueTask OnTurnStartedAsync(TurnStarted notice, CancellationToken cancellationToken) => Record(notice);

        public override ValueTask OnTurnCompletedAsync(TurnCompleted notice, CancellationToken cancellationToken) => Record(notice);

        public override ValueTask OnConversationEndedAsync(ConversationEnded notice, CancellationToken cancellationToken) => Record(notice);

        public override ValueTask OnConversationUnloadedAsync(ConversationUnloaded notice, CancellationToken cancellationToken) => Record(notice);

        public override ValueTask OnTurnRefusedAsync(TurnRefused notice, CancellationToken cancellationToken) => Record(notice);

        public override ValueTask OnHostStartedAsync(HostStarted notice, CancellationToken cancellationToken) => Record(notice);

        public override ValueTask OnFaultAsync(Fault notice, CancellationToken cancellationToken) => Record(notice);

        private async ValueTask Record(HookNotice notice)
        {
            lock (_gate)
            {
                _seen.Add(notice);
                _threads.Add(Environment.CurrentManagedThreadId);
            }

            _ = Entered.TrySetResult();
            if (Hold is { } hold)
            {
                await hold.Task;
            }
        }
    }
}

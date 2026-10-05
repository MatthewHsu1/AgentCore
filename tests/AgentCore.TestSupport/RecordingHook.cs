using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Notices;

namespace AgentCore.TestSupport
{
    /// <summary>
    /// A hook that keeps every notice it is handed, in delivery order. It never times out, so a test that
    /// flushes sees everything.
    /// </summary>
    public sealed class RecordingHook : AgentHook
    {
        private readonly Lock _gate = new();
        private readonly List<HookNotice> _notices = [];
        private readonly List<(Func<HookNotice, bool> Match, TaskCompletionSource<HookNotice> Seen)> _waiters = [];

        public IReadOnlyList<HookNotice> Notices
        {
            get
            {
                lock (_gate)
                {
                    return [.. _notices];
                }
            }
        }

        public override TimeSpan? NoticeTimeout => null;

        public IReadOnlyList<T> Of<T>()
            where T : HookNotice
        {
            return [.. Notices.OfType<T>()];
        }

        /// <summary>Completes with the first notice of type <typeparamref name="T"/> that matches, seen before or after the call.</summary>
        public async Task<T> WaitForAsync<T>(Func<T, bool>? match = null)
            where T : HookNotice
        {
            TaskCompletionSource<HookNotice> seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
            bool Matches(HookNotice notice) => notice is T typed && (match?.Invoke(typed) ?? true);

            lock (_gate)
            {
                if (_notices.FirstOrDefault(Matches) is { } already)
                {
                    return (T)already;
                }

                _waiters.Add((Matches, seen));
            }

            return (T)await seen.Task.ConfigureAwait(false);
        }

        public override ValueTask OnHostStartedAsync(HostStarted notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnHostStoppingAsync(HostStopping notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnRetentionSweptAsync(RetentionSwept notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnCallStartedAsync(CallStarted notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnLineSpokenAsync(LineSpoken notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnCallEndedAsync(CallEnded notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnConversationStartedAsync(ConversationStarted notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnConversationUnloadedAsync(ConversationUnloaded notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnConversationEndedAsync(ConversationEnded notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnTurnStartedAsync(TurnStarted notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnTurnRefusedAsync(TurnRefused notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnTurnSupersededAsync(TurnSuperseded notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnInputModeratedAsync(InputModerated notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnKnowledgeSearchedAsync(KnowledgeSearched notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnSkillLoadedAsync(SkillLoaded notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnCompactedAsync(Compacted notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnReplyUpdatedAsync(ReplyUpdated notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnModelCalledAsync(ModelCalled notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnToolCalledAsync(ToolCalled notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnApprovalChangedAsync(ApprovalChanged notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnFilePublishedAsync(FilePublished notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnSubagentStartedAsync(SubagentStarted notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnSubagentEndedAsync(SubagentEnded notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnTurnCompletedAsync(TurnCompleted notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnReplyCutAsync(ReplyCut notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnVoiceStateChangedAsync(VoiceStateChanged notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnTurnLatencyAsync(TurnLatency notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnStageChangedAsync(StageChanged notice, CancellationToken cancellationToken) => Record(notice);
        public override ValueTask OnFaultAsync(Fault notice, CancellationToken cancellationToken) => Record(notice);

        private ValueTask Record(HookNotice notice)
        {
            List<TaskCompletionSource<HookNotice>> done = [];
            lock (_gate)
            {
                _notices.Add(notice);
                foreach ((Func<HookNotice, bool> Match, TaskCompletionSource<HookNotice> Seen) waiter in _waiters.Where(w => w.Match(notice)).ToList())
                {
                    _ = _waiters.Remove(waiter);
                    done.Add(waiter.Seen);
                }
            }

            foreach (TaskCompletionSource<HookNotice> seen in done)
            {
                _ = seen.TrySetResult(notice);
            }

            return ValueTask.CompletedTask;
        }
    }
}

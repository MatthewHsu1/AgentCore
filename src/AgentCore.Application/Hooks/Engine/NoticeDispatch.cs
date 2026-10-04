using AgentCore.Application.Hooks.Notices;

namespace AgentCore.Application.Hooks.Engine
{
    /// <summary>Calls the <see cref="AgentHook"/> method that matches a notice's type.</summary>
    internal static class NoticeDispatch
    {
        internal static ValueTask DeliverAsync(AgentHook hook, HookNotice notice, CancellationToken cancellationToken)
        {
            return notice switch
            {
                HostStarted n => hook.OnHostStartedAsync(n, cancellationToken),
                HostStopping n => hook.OnHostStoppingAsync(n, cancellationToken),
                RetentionSwept n => hook.OnRetentionSweptAsync(n, cancellationToken),
                CallStarted n => hook.OnCallStartedAsync(n, cancellationToken),
                LineSpoken n => hook.OnLineSpokenAsync(n, cancellationToken),
                CallEnded n => hook.OnCallEndedAsync(n, cancellationToken),
                ConversationStarted n => hook.OnConversationStartedAsync(n, cancellationToken),
                ConversationUnloaded n => hook.OnConversationUnloadedAsync(n, cancellationToken),
                ConversationEnded n => hook.OnConversationEndedAsync(n, cancellationToken),
                TurnStarted n => hook.OnTurnStartedAsync(n, cancellationToken),
                TurnRefused n => hook.OnTurnRefusedAsync(n, cancellationToken),
                TurnSuperseded n => hook.OnTurnSupersededAsync(n, cancellationToken),
                InputModerated n => hook.OnInputModeratedAsync(n, cancellationToken),
                KnowledgeSearched n => hook.OnKnowledgeSearchedAsync(n, cancellationToken),
                SkillLoaded n => hook.OnSkillLoadedAsync(n, cancellationToken),
                Compacted n => hook.OnCompactedAsync(n, cancellationToken),
                ReplyUpdated n => hook.OnReplyUpdatedAsync(n, cancellationToken),
                ModelCalled n => hook.OnModelCalledAsync(n, cancellationToken),
                ToolCalled n => hook.OnToolCalledAsync(n, cancellationToken),
                ApprovalChanged n => hook.OnApprovalChangedAsync(n, cancellationToken),
                FilePublished n => hook.OnFilePublishedAsync(n, cancellationToken),
                SubagentStarted n => hook.OnSubagentStartedAsync(n, cancellationToken),
                SubagentEnded n => hook.OnSubagentEndedAsync(n, cancellationToken),
                TurnCompleted n => hook.OnTurnCompletedAsync(n, cancellationToken),
                ReplyCut n => hook.OnReplyCutAsync(n, cancellationToken),
                VoiceStateChanged n => hook.OnVoiceStateChangedAsync(n, cancellationToken),
                TurnLatency n => hook.OnTurnLatencyAsync(n, cancellationToken),
                StageChanged n => hook.OnStageChangedAsync(n, cancellationToken),
                Fault n => hook.OnFaultAsync(n, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(notice), notice.GetType().Name, "The notice is outside the closed set."),
            };
        }
    }
}

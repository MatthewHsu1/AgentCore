using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;

namespace AgentCore.Application.Hooks
{
    /// <summary>
    /// The one hook class. Consumers and AgentCore itself derive from it and override only what they need.
    /// </summary>
    public abstract class AgentHook
    {
        /// <summary>A transport offers a call (GPT-Live webhook, Telnyx setup frame). Decide with <see cref="CallGate.Accept"/> or <see cref="CallGate.Reject"/>.</summary>
        /// <param name="gate">The facts and the verbs of this gate.</param>
        /// <param name="cancellationToken">Cancelled at the gate's deadline.</param>
        /// <returns>A task that completes when the hook decided.</returns>
        public virtual ValueTask BeforeCallAsync(CallGate gate, CancellationToken cancellationToken) => default;

        /// <summary>Every AgentCore route, HTTP or call, needs an entry. Decide with <see cref="EntryGate.Choose"/> or <see cref="EntryGate.Refuse"/>.</summary>
        /// <param name="gate">The facts and the verbs of this gate.</param>
        /// <param name="cancellationToken">Cancelled at the gate's deadline.</param>
        /// <returns>A task that completes when the hook decided.</returns>
        public virtual ValueTask BeforeEntryAsync(EntryGate gate, CancellationToken cancellationToken) => default;

        /// <summary>The user's message arrives, before moderation and the model.</summary>
        /// <param name="gate">The facts and the verbs of this gate.</param>
        /// <param name="cancellationToken">Cancelled at the gate's deadline.</param>
        /// <returns>A task that completes when the hook decided.</returns>
        public virtual ValueTask BeforeTurnAsync(TurnGate gate, CancellationToken cancellationToken) => default;

        /// <summary>Once per run, when the context is built. A graph participant or an agent-as-tool child is a run of its own, so it fires again with <see cref="RunGate.Nested"/> set.</summary>
        /// <param name="gate">The facts and the verbs of this gate.</param>
        /// <param name="cancellationToken">Cancelled at the gate's deadline.</param>
        /// <returns>A task that completes when the hook decided.</returns>
        public virtual ValueTask BeforeRunAsync(RunGate gate, CancellationToken cancellationToken) => default;

        /// <summary>AgentCore is about to compact.</summary>
        /// <param name="gate">The facts and the verbs of this gate.</param>
        /// <param name="cancellationToken">Cancelled at the gate's deadline.</param>
        /// <returns>A task that completes when the hook decided.</returns>
        public virtual ValueTask BeforeCompactionAsync(CompactionGate gate, CancellationToken cancellationToken) => default;

        /// <summary>Before each model round trip.</summary>
        /// <param name="gate">The facts and the verbs of this gate.</param>
        /// <param name="cancellationToken">Cancelled at the gate's deadline.</param>
        /// <returns>A task that completes when the hook decided.</returns>
        public virtual ValueTask BeforeModelAsync(ModelGate gate, CancellationToken cancellationToken) => default;

        /// <summary>After each round trip, before tools run or text leaves. Overriding this method buffers every streamed model round of every turn before any of it reaches the caller.</summary>
        /// <param name="gate">The facts and the verbs of this gate.</param>
        /// <param name="cancellationToken">Cancelled at the gate's deadline.</param>
        /// <returns>A task that completes when the hook decided.</returns>
        public virtual ValueTask AfterModelAsync(ModelResultGate gate, CancellationToken cancellationToken) => default;

        /// <summary>A round trip throws.</summary>
        /// <param name="gate">The facts and the verbs of this gate.</param>
        /// <param name="cancellationToken">Cancelled at the gate's deadline.</param>
        /// <returns>A task that completes when the hook decided.</returns>
        public virtual ValueTask AfterModelFailedAsync(ModelFailureGate gate, CancellationToken cancellationToken) => default;

        /// <summary>A tool that needs approval is called.</summary>
        /// <param name="gate">The facts and the verbs of this gate.</param>
        /// <param name="cancellationToken">Cancelled at the gate's deadline.</param>
        /// <returns>A task that completes when the hook decided.</returns>
        public virtual ValueTask BeforeToolApprovalAsync(ApprovalGate gate, CancellationToken cancellationToken) => default;

        /// <summary>Before each tool call.</summary>
        /// <param name="gate">The facts and the verbs of this gate.</param>
        /// <param name="cancellationToken">Cancelled at the gate's deadline.</param>
        /// <returns>A task that completes when the hook decided.</returns>
        public virtual ValueTask BeforeToolAsync(ToolGate gate, CancellationToken cancellationToken) => default;

        /// <summary>After a tool returns.</summary>
        /// <param name="gate">The facts and the verbs of this gate.</param>
        /// <param name="cancellationToken">Cancelled at the gate's deadline.</param>
        /// <returns>A task that completes when the hook decided.</returns>
        public virtual ValueTask AfterToolAsync(ToolResultGate gate, CancellationToken cancellationToken) => default;

        /// <summary>A tool throws.</summary>
        /// <param name="gate">The facts and the verbs of this gate.</param>
        /// <param name="cancellationToken">Cancelled at the gate's deadline.</param>
        /// <returns>A task that completes when the hook decided.</returns>
        public virtual ValueTask AfterToolFailedAsync(ToolFailureGate gate, CancellationToken cancellationToken) => default;

        /// <summary>The run has a reply, before the turn is sealed.</summary>
        /// <param name="gate">The facts and the verbs of this gate.</param>
        /// <param name="cancellationToken">Cancelled at the gate's deadline.</param>
        /// <returns>A task that completes when the hook decided.</returns>
        public virtual ValueTask AfterRunAsync(RunEndGate gate, CancellationToken cancellationToken) => default;

        /// <summary>Boot finished.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnHostStartedAsync(HostStarted notice, CancellationToken cancellationToken) => default;

        /// <summary>Shutdown begins.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnHostStoppingAsync(HostStopping notice, CancellationToken cancellationToken) => default;

        /// <summary>The hourly retention sweep ran.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnRetentionSweptAsync(RetentionSwept notice, CancellationToken cancellationToken) => default;

        /// <summary>A call is accepted and the session is open.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnCallStartedAsync(CallStarted notice, CancellationToken cancellationToken) => default;

        /// <summary>A spoken line closes.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnLineSpokenAsync(LineSpoken notice, CancellationToken cancellationToken) => default;

        /// <summary>After the store opens.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnConversationStartedAsync(ConversationStarted notice, CancellationToken cancellationToken) => default;

        /// <summary>Idle unload or close; the conversation lives on in the store.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnConversationUnloadedAsync(ConversationUnloaded notice, CancellationToken cancellationToken) => default;

        /// <summary>The conversation ends. Once per conversation.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnConversationEndedAsync(ConversationEnded notice, CancellationToken cancellationToken) => default;

        /// <summary>The turn is admitted and its agent chosen.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnTurnStartedAsync(TurnStarted notice, CancellationToken cancellationToken) => default;

        /// <summary>Any refusal.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnTurnRefusedAsync(TurnRefused notice, CancellationToken cancellationToken) => default;

        /// <summary>Edit-and-resend withdrew turns.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnTurnSupersededAsync(TurnSuperseded notice, CancellationToken cancellationToken) => default;

        /// <summary>Moderation decided.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnInputModeratedAsync(InputModerated notice, CancellationToken cancellationToken) => default;

        /// <summary>A knowledge search finished.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnKnowledgeSearchedAsync(KnowledgeSearched notice, CancellationToken cancellationToken) => default;

        /// <summary>A skill was pinned or loaded.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnSkillLoadedAsync(SkillLoaded notice, CancellationToken cancellationToken) => default;

        /// <summary>Compaction finished or failed.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnCompactedAsync(Compacted notice, CancellationToken cancellationToken) => default;

        /// <summary>Each output update that leaves the turn. Overriding this method makes AgentCore raise it for every update.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnReplyUpdatedAsync(ReplyUpdated notice, CancellationToken cancellationToken) => default;

        /// <summary>Each model round trip ended.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnModelCalledAsync(ModelCalled notice, CancellationToken cancellationToken) => default;

        /// <summary>Each tool call ended. One per call.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnToolCalledAsync(ToolCalled notice, CancellationToken cancellationToken) => default;

        /// <summary>Approval asked or answered.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnApprovalChangedAsync(ApprovalChanged notice, CancellationToken cancellationToken) => default;

        /// <summary><c>file.publish</c> stored a file.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnFilePublishedAsync(FilePublished notice, CancellationToken cancellationToken) => default;

        /// <summary>An agent-as-tool run began.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnSubagentStartedAsync(SubagentStarted notice, CancellationToken cancellationToken) => default;

        /// <summary>An agent-as-tool run ended.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnSubagentEndedAsync(SubagentEnded notice, CancellationToken cancellationToken) => default;

        /// <summary>The turn is sealed.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnTurnCompletedAsync(TurnCompleted notice, CancellationToken cancellationToken) => default;

        /// <summary>The caller spoke over the reply (cut or recut).</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnReplyCutAsync(ReplyCut notice, CancellationToken cancellationToken) => default;

        /// <summary>The user or agent voice state changed.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnVoiceStateChangedAsync(VoiceStateChanged notice, CancellationToken cancellationToken) => default;

        /// <summary>Voice turn timing is known.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnTurnLatencyAsync(TurnLatency notice, CancellationToken cancellationToken) => default;

        /// <summary>The stage machine moved.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnStageChangedAsync(StageChanged notice, CancellationToken cancellationToken) => default;

        /// <summary>A non-fatal fault.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnFaultAsync(Fault notice, CancellationToken cancellationToken) => default;

        /// <summary>A started call left. Once per call, also after <see cref="ConversationEnded"/>.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="cancellationToken">Cancelled when <see cref="NoticeTimeout"/> passes.</param>
        /// <returns>A task that completes when the hook is done with the notice.</returns>
        public virtual ValueTask OnCallEndedAsync(CallEnded notice, CancellationToken cancellationToken) => default;

        /// <summary>Gets how a gate of this hook fails. Override per point to fail closed where the gate fails open.</summary>
        /// <param name="point">The gate point.</param>
        /// <returns>The point's <see cref="GatePoint.DefaultFailure"/> unless overridden.</returns>
        public virtual HookFailure FailureFor(GatePoint point)
        {
            ArgumentNullException.ThrowIfNull(point);
            return point.DefaultFailure;
        }

        /// <summary>
        /// Gets how long one notice may take before AgentCore abandons it and goes on, or <see langword="null"/> to
        /// wait for every notice, logging every 30 s while it waits.
        /// </summary>
        public virtual TimeSpan? NoticeTimeout => TimeSpan.FromSeconds(30);
    }
}

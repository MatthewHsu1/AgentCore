using System.Collections.Concurrent;
using AgentCore.Application.Hooks.Notices;

namespace AgentCore.Application.Hooks.Engine
{
    /// <summary>
    /// The notices of one loaded session. Every raise site of the session and of its turn's layers goes through
    /// this (the turn carries it as <c>TurnInvocation.Hooks</c>). It pins the conversation's mailbox while the
    /// session is loaded, so an idle eviction never restarts the conversation's <c>Sequence</c>.
    /// </summary>
    internal sealed class SessionHooks
    {
        private readonly HookRuntime _runtime;

        private readonly string _conversationId;

        private readonly string _entry;

        private readonly TimeProvider _time;

        private int _ended;

        private int _started;

        private int _released;

        internal SessionHooks(HookRuntime runtime, string conversationId, string entry, TimeProvider time)
        {
            _runtime = runtime;
            _conversationId = conversationId;
            _entry = entry;
            _time = time;
            runtime.Notices.Pin(conversationId);
        }

        internal HookRuntime Runtime => _runtime;

        internal string ConversationId => _conversationId;

        /// <summary>Gets this session instance's id: a version 7 Guid, so it sorts by load time.</summary>
        internal Guid SessionId { get; } = Guid.CreateVersion7();

        internal TimeProvider Time => _time;

        /// <summary>
        /// Gets the BeforeToolApproval decisions still open, by function call id: a denial the deny layer has not answered yet, or a call
        /// left to a human who has not answered yet. MAF asks the auto rule again about every queued request on every run,
        /// so this is what keeps the hooks to one decision per call.
        /// </summary>
        internal ConcurrentDictionary<string, ApprovalDecision> ApprovalDecisions { get; } = new(StringComparer.Ordinal);

        internal HookScope Scope(int? turnIndex, string? stage)
        {
            return new(_conversationId, _entry, turnIndex, stage, SessionId, 0, _time.GetUtcNow());
        }

        internal bool Wants<TNotice>()
            where TNotice : HookNotice
        {
            return _runtime.Notices.Wants(typeof(TNotice));
        }

        /// <summary>Raises one notice of this session.</summary>
        /// <param name="notice">The fact.</param>
        /// <param name="occurredAt">When it happened, if it was read before the raise (a turn's end), or <see langword="null"/> for now.</param>
        internal Guid Raise(HookNotice notice, DateTimeOffset? occurredAt = null)
        {
            return _runtime.Notices.Raise(notice, _time, occurredAt: occurredAt);
        }

        /// <summary>
        /// Raises <see cref="ConversationStarted"/>, once per session. The caller holds the session's turn lock, so the
        /// start always takes a lower <c>Sequence</c> than the end or refusal that may make it.
        /// </summary>
        internal void RaiseStarted(ConversationOrigin origin)
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _ = Raise(new ConversationStarted(Scope(turnIndex: null, stage: null), origin));
            }
        }

        /// <summary>
        /// Raises the start of a session that ends or refuses a turn before it opened its store, so no end or refusal is
        /// ever the first fact of a session. The caller holds the session's turn lock.
        /// </summary>
        internal void EnsureStarted()
        {
            if (Volatile.Read(ref _started) == 0)
            {
                RaiseStarted(_runtime.Notices.LoadedBefore(_conversationId, SessionId) ? ConversationOrigin.Reloaded : ConversationOrigin.Unopened);
            }
        }

        /// <summary>Raises the end, once. From then on, only notices of turns up to <paramref name="lastAdmittedTurn"/> get through.</summary>
        /// <returns><see langword="false"/> when this session had already ended.</returns>
        internal bool RaiseEnd(ConversationEnded notice, int lastAdmittedTurn)
        {
            if (Interlocked.Exchange(ref _ended, 1) == 1)
            {
                return false;
            }

            _ = _runtime.Notices.Raise(notice, _time, endsAfterTurn: lastAdmittedTurn);
            return true;
        }

        internal void RaiseFault(Fault fault, AgentHook except)
        {
            _ = _runtime.Notices.Raise(fault, _time, except: except);
        }

        internal ConversationOrigin OriginOf(bool storeHeldConversation)
        {
            if (_runtime.Notices.LoadedBefore(_conversationId, SessionId))
            {
                return ConversationOrigin.Reloaded;
            }

            return storeHeldConversation ? ConversationOrigin.Resumed : ConversationOrigin.New;
        }

        internal Task FlushAsync()
        {
            return _runtime.Notices.FlushAsync(_conversationId);
        }

        /// <summary>Lets go of the mailbox pin, once. The session was disposed or unloaded.</summary>
        internal void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _runtime.Notices.Release(_conversationId);
            }
        }
    }
}

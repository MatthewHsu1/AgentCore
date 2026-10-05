using System.Runtime.CompilerServices;
using AgentCore.Application.Conversation;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Layers;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Domain.Audit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Calls
{
    /// <summary>
    /// One phone call between a voice vendor and the engine. A vendor adapter turns its wire
    /// into these calls; this class knows no wire and no CRM.
    /// </summary>
    internal sealed class PhoneCall
    {
        private readonly CallOffer _offer;

        private readonly Action<PhoneCall>? _replaced;

        private readonly Action _holder;

        private readonly CallEndNotice _end;

        private readonly CallBrief _brief;

        private ConversationSession _session;

        private DateTimeOffset? _keptStart;

        private StrongBox<DateTimeOffset>? _startedAt;

        private int _startCalled;

        private int _ended;

        private int _linesDropped;

        private int _wasReplaced;

        private PhoneCall(PhoneCallHost host, CallOffer offer, string conversationId, string? brief, ConversationSession session, Action<PhoneCall>? replaced)
        {
            Host = host;
            _offer = offer;
            _replaced = replaced;
            _holder = OnReplaced;
            _end = new CallEndNotice(offer.CallId, host.Time, host.Logger);
            _session = session;
            ConversationId = conversationId;
            _brief = new CallBrief(host, conversationId, brief);
            Asks = new CallAsks(this);
        }

        internal PhoneCallHost Host { get; }

        internal string CallId => _offer.CallId;

        internal string Entry => _offer.Entry;

        internal string ConversationId { get; }

        /// <summary>Gets the brief the hook accepted the call with, for the vendor's own voice; <see langword="null"/> for none.</summary>
        internal string? Brief => _brief.Text;

        internal ConversationSession Session => Volatile.Read(ref _session);

        internal DateTimeOffset StartedAt => Volatile.Read(ref _startedAt)?.Value ?? throw new InvalidOperationException("The transport has not taken this call yet.");

        internal bool HasEnded => Volatile.Read(ref _ended) == 1;

        /// <summary>Gets whether a newer call with this id took the conversation over; this call ends and closes nothing.</summary>
        internal bool WasReplaced => Volatile.Read(ref _wasReplaced) == 1;

        /// <summary>
        /// Gets whether the engine ended the call's conversation (a terminal stage, a tool) while the call is still up.
        /// </summary>
        internal bool EngineEnded => Session.Lifetime.Ending.Requested && !HasEnded;

        /// <summary>
        /// Gets whether <see cref="StartAsync"/> got past its last check and stamped the start time. The session's call
        /// mark, the brief and <see cref="CallStarted"/> may still be on their way.
        /// </summary>
        internal bool HasStarted => Volatile.Read(ref _startedAt) is not null;

        /// <summary>Gets the engine turns the vendor asks for, one per delegation.</summary>
        internal CallAsks Asks { get; }

        internal CallChannel Channel { get; } = new();

        /// <summary>
        /// Runs the call gate, then opens the conversation the hook named. A conversation that
        /// a call with another id holds refuses the call as busy: one live call per conversation. A live call
        /// with the same id is taken over, as in LiveKit; the older call's <c>replaced</c> then runs on this admission,
        /// so the vendor can drop its connection, and must not block.
        /// </summary>
        internal static async ValueTask<PhoneCallAdmission> AdmitAsync(PhoneCallHost host, CallOffer offer, CancellationToken cancellationToken, Action<PhoneCall>? replaced = null)
        {
            ArgumentNullException.ThrowIfNull(host);
            ArgumentNullException.ThrowIfNull(offer);

            CallDecision decision = await CallGateChain.DecideAsync(host.Hooks, offer, cancellationToken, host.AnswerDeadline).ConfigureAwait(false);
            if (!decision.Accepted || decision.ConversationId is not { } conversationId)
            {
                CallRefusal refusal = decision.Refusal ?? CallRefusal.Unavailable;
                CallLog.CallRefused(host.Logger, offer.CallId, offer.Transport, refusal);
                return PhoneCallAdmission.Refused(refusal);
            }

            ConversationSession session;
            try
            {
                session = await host.Sessions.GetOrOpenAsync(offer.Entry, conversationId, state: null, cancellationToken).ConfigureAwait(false);
            }
            catch (ConversationInUseException)
            {
                CallLog.ConversationInUse(host.Logger, offer.CallId, conversationId, offer.Entry);
                return PhoneCallAdmission.Refused(CallRefusal.Busy);
            }

            PhoneCall call = new(host, offer, conversationId, decision.Brief, session, replaced);
            if (!session.Lifetime.Ending.ClaimCall(offer.CallId, call._holder, out call._keptStart))
            {
                CallLog.ConversationHeldByCall(host.Logger, offer.CallId, conversationId);
                return PhoneCallAdmission.RefusedHeldByCall();
            }

            return PhoneCallAdmission.Admitted(call);
        }

        /// <summary>
        /// The transport took the call: opens the store, marks the session as the call's, takes over the brief of the
        /// conversation, and raises <see cref="CallStarted"/>. A call starts once, and never after it left. When the
        /// admitted session unloaded, the call starts on its conversation reopened. A call that took over a started
        /// call with its id keeps that start and raises no second <see cref="CallStarted"/>: it is the same call.
        /// </summary>
        /// <exception cref="InvalidOperationException">The call already started, or it ended or was abandoned, also while the store opened.</exception>
        /// <exception cref="CallConversationLostException">
        /// Another call took the conversation after an unload, or a newer call with this id took it over. Its session
        /// and its brief are left untouched.
        /// </exception>
        internal async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            if (HasEnded)
            {
                throw new InvalidOperationException("The call has already left.");
            }

            if (Interlocked.Exchange(ref _startCalled, 1) == 1)
            {
                throw new InvalidOperationException("The call has already started.");
            }

            ConversationSession session = await Host.Sessions.GetOrOpenAsync(Entry, ConversationId, state: null, cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(session, Session))
            {
                if (!session.Lifetime.Ending.ClaimCall(CallId, _holder, out DateTimeOffset? kept))
                {
                    throw new CallConversationLostException(CallId, ConversationId);
                }

                _keptStart = kept;
                Volatile.Write(ref _session, session);
            }

            // ConversationStarted is raised when the store opens, so it always comes before CallStarted.
            _ = await session.Ledger.OpenSessionAsync(cancellationToken).ConfigureAwait(false);

            if (HasEnded)
            {
                throw new InvalidOperationException("The call left while its conversation opened.");
            }

            DateTimeOffset startedAt = _keptStart ?? Host.Time.GetUtcNow();
            if (!session.Lifetime.Ending.TryMarkCall(CallId, _holder, startedAt))
            {
                throw new CallConversationLostException(CallId, ConversationId);
            }

            Volatile.Write(ref _startedAt, new StrongBox<DateTimeOffset>(startedAt));

            _brief.File();

            // An end that landed after the check above may have forgotten the brief before it was set here, and it
            // raises its own end: the call does not start.
            if (HasEnded)
            {
                _brief.Clear();
                return;
            }

            if (_keptStart is null)
            {
                SessionHooks hooks = session.Hooks;
                _ = hooks.Raise(new CallStarted(hooks.Scope(turnIndex: null, stage: null), CallId, _offer.From, _offer.To, _offer.Transport));
            }

            _end.Started(startedAt);
        }

        /// <summary>Lets go of an admitted call the transport never took. No call notice is raised.</summary>
        internal async ValueTask AbandonAsync()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 1)
            {
                return;
            }

            await CloseAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Raises one closed spoken line as <see cref="LineSpoken"/>, and marks the conversation as still live. After an
        /// unload, a session reopened with no call on it is taken over, as <see cref="LiveSessionAsync"/> does; a line
        /// heard after another call took the conversation is dropped (and logged once), never raised into that call's.
        /// </summary>
        /// <param name="speaker">Who spoke the line.</param>
        /// <param name="text">The line's words.</param>
        /// <param name="startedAt">When the line began, or <see langword="null"/> for now.</param>
        /// <param name="endedAt">When the line ended, or <see langword="null"/> for now.</param>
        /// <param name="turnIndex">
        /// For an agent line, the turn whose reply it speaks; a caller line carries none. Either line still reaches
        /// the hooks after <see cref="ConversationEnded"/>: a spoken line is a record of the call.
        /// </param>
        internal async ValueTask HeardAsync(Speaker speaker, string text, DateTimeOffset? startedAt = null, DateTimeOffset? endedAt = null, int? turnIndex = null)
        {
            ArgumentNullException.ThrowIfNull(text);
            if (HasEnded || text.Length == 0)
            {
                return;
            }

            ConversationSession target = Session;
            ConversationSession? holder = await HolderAsync(target).ConfigureAwait(false);
            if (holder is not null && !ReferenceEquals(holder, target))
            {
                if (Volatile.Read(ref _startedAt) is not { } started || !TryAdopt(holder, started.Value))
                {
                    if (Interlocked.Exchange(ref _linesDropped, 1) == 0)
                    {
                        CallLog.LinesDropped(Host.Logger, CallId, ConversationId);
                    }

                    return;
                }

                target = holder;
            }

            DateTimeOffset now = Host.Time.GetUtcNow();
            SessionHooks hooks = target.Hooks;
            int? lineTurn = speaker == Speaker.Agent ? turnIndex : null;
            _ = hooks.Raise(new LineSpoken(hooks.Scope(lineTurn, stage: null), speaker, text, startedAt ?? now, endedAt ?? now));
        }

        /// <summary>
        /// Ends the call's conversation once and forgets its brief; a second end does nothing. The session stays
        /// open: call <see cref="CloseAsync"/>.
        /// </summary>
        internal async ValueTask EndAsync(ConversationEndReason reason, string? cause)
        {
            if (Interlocked.Exchange(ref _ended, 1) == 1)
            {
                return;
            }

            await Asks.StopAsync().ConfigureAwait(false);

            ConversationSession own = Session;
            ConversationSession? holder = await HolderAsync(own).ConfigureAwait(false);
            if (!Leaves(holder, own))
            {
                return;
            }

            if (Owns(holder, own))
            {
                try
                {
                    _ = own.Lifetime.EndConversation(reason, cause);
                }
                catch (Exception fault) when (fault is not OperationCanceledException)
                {
                    CallLog.CallEndFaulted(Host.Logger, CallId, ConversationId, fault);
                }
            }

            _end.Left(holder, own, CallEndReason.Ended, cause);
            _brief.Forget(holder, own);
        }

        /// <summary>
        /// Closes the call's session with no end notice, unless the id moved on to a newer session or a newer call
        /// took it over, and forgets the brief. Every way a call leaves comes through here or <see cref="EndAsync"/>.
        /// </summary>
        internal async ValueTask CloseAsync()
        {
            ConversationSession own = Session;
            ConversationSession? holder = await HolderAsync(own).ConfigureAwait(false);
            if (!Leaves(holder, own))
            {
                return;
            }

            _end.Left(holder, own, CallEndReason.Closed, cause: null);
            _brief.Forget(holder, own);
            if (Owns(holder, own))
            {
                await Host.Sessions.CloseAsync(Entry, ConversationId, CancellationToken.None).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The session the next ask runs on: the held one, or the call's conversation reopened after an idle unload and
        /// marked as the call's. A reopened session with no call on it is taken over.
        /// </summary>
        /// <exception cref="InvalidOperationException">The transport has not taken this call yet.</exception>
        /// <exception cref="CallConversationLostException">
        /// Another call took the conversation after the unload. That call, its end, and its brief are left untouched.
        /// </exception>
        internal async ValueTask<ConversationSession> LiveSessionAsync(CancellationToken cancellationToken)
        {
            DateTimeOffset startedAt = StartedAt;
            ConversationSession live = await Host.Sessions.GetOrOpenAsync(Entry, ConversationId, state: null, cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(live, Session) && !TryAdopt(live, startedAt))
            {
                throw new CallConversationLostException(CallId, ConversationId);
            }

            return live;
        }

        // Takes over the call's conversation reopened after an unload, unless another call holds it.
        private bool TryAdopt(ConversationSession reopened, DateTimeOffset startedAt)
        {
            if (WasReplaced || !reopened.Lifetime.Ending.TryMarkCall(CallId, _holder, startedAt))
            {
                return false;
            }

            Volatile.Write(ref _session, reopened);
            Channel.Follow(reopened);
            return true;
        }

        internal static bool Owns(ConversationSession? holder, ConversationSession own)
        {
            return holder is null || ReferenceEquals(holder, own);
        }

        // Once the call lets go of its session, no newer call takes it over; one that did owns it, its end and brief.
        private bool Leaves(ConversationSession? holder, ConversationSession own)
        {
            return !Owns(holder, own) || own.Lifetime.Ending.ReleaseCall(_holder);
        }

        private void OnReplaced()
        {
            Volatile.Write(ref _wasReplaced, 1);
            CallLog.CallReplaced(Host.Logger, CallId, ConversationId);
            _end.Left(Session, CallEndReason.Replaced, cause: null);
            _replaced?.Invoke(this);
        }

        // The id may have moved on: this call's session unloaded and a later caller reopened it. Ending or closing that
        // newer session would answer this call's hang-up with someone else's call. An unreadable store falls back to
        // this call's own session. The lookup takes no token, so a cancellation here is the store's own timeout: a fault
        // like any other.
        private async ValueTask<ConversationSession?> HolderAsync(ConversationSession own)
        {
            try
            {
                return await Host.Sessions.TryGetAsync(Entry, ConversationId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception fault) when (fault is not OutOfMemoryException)
            {
                CallLog.SessionTouchFaulted(Host.Logger, ConversationId, fault);
                return own;
            }
        }
    }
}

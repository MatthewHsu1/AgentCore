using AgentCore.Application.Hooks.Notices;

namespace AgentCore.Application.Hooks.Engine
{
    /// <summary>
    /// The notices of one conversation id, or of the host. Its private lock makes the <c>Sequence</c> count and the
    /// channel writes one step, so <c>Sequence</c> order is delivery order. No turn code holds this lock,
    /// and no hook code runs under it.
    /// </summary>
    internal sealed class Mailbox
    {
        private readonly Lock _lock = new();

        private readonly string? _conversationId;

        private readonly HookReader[] _readers;

        private long _sequence;

        private Guid _lastSession;

        private (Guid Session, int LastTurn)? _ended;

        private int _pins;

        private bool _closed;

        internal Mailbox(NoticeHub hub, string? conversationId)
        {
            Hub = hub;
            _conversationId = conversationId;
            _readers = [.. hub.Table.NoticeHooks.Select(hook => new HookReader(this, hook, hub.Table.NoticesOf(hook)))];
        }

        internal NoticeHub Hub { get; }

        internal IEnumerable<Task> Readers => _readers.Select(static reader => reader.Completion);

        /// <summary>Stamps and queues one notice.</summary>
        /// <returns><see langword="false"/> when this mailbox was closed and the caller must look up a new one.</returns>
        internal bool TryRaise(
            HookNotice notice, TimeProvider clock, int? endsAfterTurn, AgentHook? except, DateTimeOffset? occurredAt, out Guid eventId)
        {
            // A version 7 Guid reads the wall clock, which can cost microseconds; its random bits never sort
            // within one millisecond anyway, so it is made before the lock and only stamped under it.
            eventId = Guid.CreateVersion7();
            lock (_lock)
            {
                if (_closed)
                {
                    return false;
                }

                HookScope scope = notice.Scope;
                if (HeldBackByTheEnd(notice))
                {
                    return true;
                }

                HookNotice stamped = notice with
                {
                    Scope = scope with { Sequence = ++_sequence, OccurredAt = occurredAt ?? clock.GetUtcNow() },
                    EventId = eventId,
                };

                if (scope.SessionId != Guid.Empty)
                {
                    _lastSession = scope.SessionId;
                }

                if (stamped is ConversationEnded)
                {
                    _ended = (scope.SessionId, endsAfterTurn ?? -1);
                }

                foreach (HookReader reader in _readers)
                {
                    if (!ReferenceEquals(reader.Hook, except) && reader.Wants(stamped))
                    {
                        reader.Write(stamped);
                    }
                }

                return true;
            }
        }

        /// <summary>Records an end no hook hears, so the ended rule still holds back the session's later turns.</summary>
        internal void MarkEnded(Guid sessionId, int? endsAfterTurn)
        {
            lock (_lock)
            {
                _ended = (sessionId, endsAfterTurn ?? -1);
            }
        }

        internal bool LoadedBefore(Guid sessionId)
        {
            lock (_lock)
            {
                return !_closed && _lastSession != Guid.Empty && _lastSession != sessionId;
            }
        }

        internal Task FlushAsync()
        {
            lock (_lock)
            {
                return _closed ? Task.CompletedTask : Task.WhenAll(_readers.Select(static reader => reader.Barrier()));
            }
        }

        /// <summary>Keeps this mailbox while a session is loaded, so its <c>Sequence</c> does not restart.</summary>
        /// <returns><see langword="false"/> when this mailbox was closed and the caller must look up a new one.</returns>
        internal bool TryPin()
        {
            lock (_lock)
            {
                if (_closed)
                {
                    return false;
                }

                _pins++;
                return true;
            }
        }

        /// <summary>Lets go of one pin. A mailbox no notice ever reached has no reader to evict it, so the last release does.</summary>
        internal void Release()
        {
            lock (_lock)
            {
                if (_pins == 0)
                {
                    return;
                }

                _pins--;
                if (_pins == 0 && !_closed && !Array.Exists(_readers, static reader => reader.Started))
                {
                    CloseAndForgetLocked();
                }
            }
        }

        /// <summary>Removes this mailbox when no session pins it and every reader is idle. A racing raise then makes a new one.</summary>
        internal bool TryEvict()
        {
            lock (_lock)
            {
                if (_closed)
                {
                    return true;
                }

                if (_pins > 0 || !Array.TrueForAll(_readers, static reader => reader.IsIdle))
                {
                    return false;
                }

                CloseAndForgetLocked();
                return true;
            }
        }

        /// <summary>Takes no more notices. The readers finish what is queued, then end.</summary>
        internal void Close()
        {
            lock (_lock)
            {
                _closed = true;
                foreach (HookReader reader in _readers)
                {
                    reader.Complete();
                }
            }
        }

        // After ConversationEnded, only the turns admitted before the end get through. Five facts are
        // exempt: a refusal records no words and says AfterEnd itself; the unload after the end and
        // a Fault about the conversation itself (a hook that failed on ConversationEnded, say) must still reach the
        // other hooks; a LineSpoken is a record of the call, not a turn notice, so the caller's goodbye after the
        // end still reaches the transcript; a CallEnded closes the call that the end left on the line. A Fault
        // inside a turn follows its turn, so it never outruns that turn's notices.
        private bool HeldBackByTheEnd(HookNotice notice)
        {
            HookScope scope = notice.Scope;
            return _ended is { } ended
                && scope.SessionId == ended.Session
                && notice is not (TurnRefused or ConversationUnloaded or LineSpoken or CallEnded or Fault { Scope.TurnIndex: null })
                && !(scope.TurnIndex is int turn && turn <= ended.LastTurn);
        }

        private void CloseAndForgetLocked()
        {
            _closed = true;
            Hub.Forget(_conversationId, this);
            foreach (HookReader reader in _readers)
            {
                reader.Complete();
            }
        }
    }
}

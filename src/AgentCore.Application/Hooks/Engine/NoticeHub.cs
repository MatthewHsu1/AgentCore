using System.Collections.Concurrent;
using AgentCore.Application.Hooks.Notices;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Hooks.Engine
{
    /// <summary>Raises notices: one mailbox per conversation id, one for the host. It never runs hook code.</summary>
    /// <param name="table">Which hooks want which notices.</param>
    /// <param name="logger">Where a failing or slow hook is reported.</param>
    /// <param name="timers">The clock of the notice timeouts, the slow-hook log, and the idle eviction.</param>
    internal sealed class NoticeHub(HookTable table, ILogger logger, TimeProvider timers)
    {
        internal static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(60);

        internal static readonly TimeSpan StillWaitingEvery = TimeSpan.FromSeconds(30);

        private readonly ConcurrentDictionary<string, Mailbox> _mailboxes = new(StringComparer.Ordinal);

        private Mailbox? _host;

        private int _stopped;

        internal HookTable Table => table;

        internal ILogger Logger => logger;

        internal TimeProvider Timers => timers;

        internal int MailboxCount => _mailboxes.Count;

        internal bool Wants(Type noticeType)
        {
            return table.Wants(noticeType);
        }

        /// <summary>Stamps one notice and queues it for every hook that wants it.</summary>
        /// <param name="notice">The fact, with its scope's conversation, session and turn already set.</param>
        /// <param name="clock">The clock <see cref="HookScope.OccurredAt"/> is read from: the session's.</param>
        /// <param name="endsAfterTurn">For <see cref="ConversationEnded"/>: the last turn admitted before the end, or -1.</param>
        /// <param name="except">A hook that must not get this notice: the one a <c>Fault(HookFailed)</c> is about.</param>
        /// <param name="occurredAt">
        /// The moment the fact happened, when the raiser read it earlier than the raise (a turn's end), or
        /// <see langword="null"/> to stamp it from <paramref name="clock"/>.
        /// </param>
        /// <returns>The notice's <see cref="HookNotice.EventId"/>. A notice nobody wants still gets a fresh one.</returns>
        internal Guid Raise(
            HookNotice notice, TimeProvider clock, int? endsAfterTurn = null, AgentHook? except = null, DateTimeOffset? occurredAt = null)
        {
            ArgumentNullException.ThrowIfNull(notice);
            ArgumentNullException.ThrowIfNull(clock);

            if (!table.Wants(notice.GetType()))
            {
                // The ended rule matters only where readers exist, so an end nobody hears never makes a mailbox.
                if (notice is ConversationEnded && notice.Scope.ConversationId is { } id && _mailboxes.TryGetValue(id, out Mailbox? box))
                {
                    box.MarkEnded(notice.Scope.SessionId, endsAfterTurn);
                }

                return Guid.CreateVersion7();
            }

            while (Volatile.Read(ref _stopped) == 0)
            {
                if (MailboxFor(notice.Scope.ConversationId).TryRaise(notice, clock, endsAfterTurn, except, occurredAt, out Guid eventId))
                {
                    return eventId;
                }
            }

            return Guid.CreateVersion7();
        }

        /// <summary>Completes when every notice raised so far for this conversation was handled by every hook.</summary>
        internal Task FlushAsync(string? conversationId)
        {
            Mailbox? box = conversationId is null ? Volatile.Read(ref _host) : _mailboxes.GetValueOrDefault(conversationId);
            return box?.FlushAsync() ?? Task.CompletedTask;
        }

        /// <summary>Completes when every notice raised so far, in every conversation and on the host, was handled.</summary>
        internal Task FlushAllAsync()
        {
            List<Task> barriers = [.. _mailboxes.Values.Select(static box => box.FlushAsync())];
            if (Volatile.Read(ref _host) is { } host)
            {
                barriers.Add(host.FlushAsync());
            }

            return Task.WhenAll(barriers);
        }

        /// <summary>Whether this process still holds the conversation's mailbox from another session.</summary>
        internal bool LoadedBefore(string conversationId, Guid sessionId)
        {
            return _mailboxes.TryGetValue(conversationId, out Mailbox? box) && box.LoadedBefore(sessionId);
        }

        /// <summary>
        /// Keeps the conversation's mailbox, and so its <c>Sequence</c>, while a session of it is loaded. Every pin
        /// is matched by one <see cref="Release"/>.
        /// </summary>
        internal void Pin(string conversationId)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            if (table.NoticeHooks.Count == 0)
            {
                return;
            }

            while (Volatile.Read(ref _stopped) == 0 && !MailboxFor(conversationId).TryPin())
            {
                // The mailbox was evicted between the lookup and the pin; the next lookup makes a new one.
            }
        }

        /// <summary>Lets go of one <see cref="Pin"/>. The mailbox is evicted once it is idle and no pin is left.</summary>
        internal void Release(string conversationId)
        {
            ArgumentNullException.ThrowIfNull(conversationId);

            if (_mailboxes.TryGetValue(conversationId, out Mailbox? box))
            {
                box.Release();
            }
        }

        /// <summary>Closes every mailbox and waits, up to <paramref name="timeout"/>, for the queued notices.</summary>
        internal async ValueTask StopAsync(TimeSpan timeout)
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 1)
            {
                return;
            }

            List<Mailbox> boxes = [.. _mailboxes.Values];
            if (Volatile.Read(ref _host) is { } host)
            {
                boxes.Add(host);
            }

            foreach (Mailbox box in boxes)
            {
                box.Close();
            }

            Task drained = Task.WhenAll(boxes.SelectMany(static box => box.Readers));

            // A timer refuses a wait past HookReader.LongestTimeout; one that long is no limit at all.
            if (timeout == Timeout.InfiniteTimeSpan || timeout > HookReader.LongestTimeout)
            {
                await drained.ConfigureAwait(false);
                return;
            }

            try
            {
                await drained.WaitAsync(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout, timers).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                int draining = boxes.Count(static box => box.Readers.Any(static reader => !reader.IsCompleted));
                HookLog.DrainTimedOut(logger, draining, (long)timeout.TotalMilliseconds);
            }
        }

        internal void Forget(string? conversationId, Mailbox box)
        {
            if (conversationId is null)
            {
                _ = Interlocked.CompareExchange(ref _host, null, box);
                return;
            }

            _ = _mailboxes.TryRemove(KeyValuePair.Create(conversationId, box));
        }

        // A mailbox GetOrAdd builds but a racing caller discards never gets a write, so it never starts a reader.
        private Mailbox MailboxFor(string? conversationId)
        {
            if (conversationId is not null)
            {
                return _mailboxes.GetOrAdd(conversationId, static (id, hub) => new Mailbox(hub, id), this);
            }

            if (Volatile.Read(ref _host) is { } host)
            {
                return host;
            }

            Mailbox made = new(this, null);
            return Interlocked.CompareExchange(ref _host, made, null) ?? made;
        }
    }
}

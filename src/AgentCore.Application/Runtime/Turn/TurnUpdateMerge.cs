using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Turn
{
    /// <summary>
    /// Interleaves a turn's <see cref="TurnNotices"/> with the run's own update stream.
    /// </summary>
    internal static class TurnUpdateMerge
    {
        /// <summary>Runs <paramref name="inner"/>, yielding <paramref name="notices"/> as they arrive.</summary>
        /// <param name="inner">The run's own stream. This owns and disposes its enumerator.</param>
        /// <param name="notices">This turn's notice channel. Completed once <paramref name="inner"/> ends or fails.</param>
        /// <param name="cancellationToken">Cancels the run.</param>
        internal static async IAsyncEnumerable<AgentResponseUpdate> RunAsync(
            IAsyncEnumerable<AgentResponseUpdate> inner,
            TurnNotices notices,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(notices);

            ChannelReader<NoticeContent> reader = notices.Reader;
            await using IAsyncEnumerator<AgentResponseUpdate> e = inner.GetAsyncEnumerator(cancellationToken);
            Task<bool>? pendingMove = null;
            Task<bool>? pendingNotice = null;

            try
            {
                while (true)
                {
                    pendingMove ??= e.MoveNextAsync().AsTask();
                    pendingNotice ??= reader.WaitToReadAsync(cancellationToken).AsTask();

                    Task finished = await Task.WhenAny(pendingMove, pendingNotice).ConfigureAwait(false);

                    if (finished == pendingNotice)
                    {
                        bool hasNotice = await pendingNotice.ConfigureAwait(false);
                        pendingNotice = null;

                        if (hasNotice)
                        {
                            while (reader.TryRead(out NoticeContent? notice))
                            {
                                yield return ToUpdate(notice);
                            }

                            continue;
                        }

                        // The channel completed with nothing queued; fall through to the inner stream.
                    }

                    // A notice that landed while the inner update was on its way out goes first, so
                    // order is preserved: nothing lets an update jump ahead of a notice that beat it.
                    while (reader.TryRead(out NoticeContent? notice))
                    {
                        yield return ToUpdate(notice);
                    }

                    bool has = await pendingMove.ConfigureAwait(false);
                    pendingMove = null;

                    if (!has)
                    {
                        break;
                    }

                    yield return e.Current;
                }
            }
            finally
            {
                notices.Complete();

                if (pendingMove is { IsCompleted: false })
                {
                    // A compiler-generated async iterator cannot be disposed while suspended at an
                    // await (NotSupportedException); on cancellation the pending MoveNextAsync is
                    // already unwinding on the same token, so it is settled here first.
                    try
                    {
                        await pendingMove.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }

            // A notice a provider posts from its post-run hook lands after the inner stream reports
            // completion, so whatever is left in the channel is drained once more here.
            while (reader.TryRead(out NoticeContent? notice))
            {
                yield return ToUpdate(notice);
            }
        }

        private static AgentResponseUpdate ToUpdate(NoticeContent content)
        {
            return new AgentResponseUpdate(ChatRole.Assistant, [content]);
        }
    }
}

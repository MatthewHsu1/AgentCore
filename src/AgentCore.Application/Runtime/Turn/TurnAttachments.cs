using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Turn
{
    /// <summary>
    /// What a turn's tools have produced for the caller and not yet attached to a message, filed under
    /// the outermost tool call that produced it.
    /// </summary>
    /// <typeparam name="TContent">The content one publish files.</typeparam>
    internal abstract class TurnAttachments<TContent> : ITurnAttachments
        where TContent : AIContent
    {
        private readonly Lock _gate = new();

        private readonly Dictionary<string, List<TContent>> _byCallId = new(StringComparer.Ordinal);

        /// <summary>Takes what was filed under one outer tool call, in publish order.</summary>
        /// <param name="callId">The call whose content to take.</param>
        /// <returns>What that call filed, or empty.</returns>
        internal IReadOnlyList<TContent> TakeFor(string callId)
        {
            ArgumentNullException.ThrowIfNull(callId);

            lock (_gate)
            {
                return _byCallId.Remove(callId, out List<TContent>? filed) ? filed : [];
            }
        }

        IReadOnlyList<AIContent> ITurnAttachments.TakeFor(string callId)
        {
            return TakeFor(callId);
        }

        /// <summary>Takes everything filed so far, under every call, in the order the calls first filed.</summary>
        /// <returns>What was filed, or empty.</returns>
        internal IReadOnlyList<TContent> TakeAll()
        {
            lock (_gate)
            {
                List<TContent> taken = [.. _byCallId.Values.SelectMany(static filed => filed)];
                _byCallId.Clear();
                return taken;
            }
        }

        /// <summary>Files one content under a call. A later publish of the same thing wins, in the place the earlier one took, so publish order is kept.</summary>
        /// <param name="callId">The outer call to file under.</param>
        /// <param name="content">What to file.</param>
        /// <param name="sameAs">Whether an already filed content is the same thing as <paramref name="content"/>.</param>
        protected void Attach(string callId, TContent content, Predicate<TContent> sameAs)
        {
            lock (_gate)
            {
                if (!_byCallId.TryGetValue(callId, out List<TContent>? filed))
                {
                    filed = [];
                    _byCallId[callId] = filed;
                }

                int index = filed.FindIndex(sameAs);
                if (index >= 0)
                {
                    filed[index] = content;
                }
                else
                {
                    filed.Add(content);
                }
            }
        }
    }
}

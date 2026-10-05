using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.ToolCalls
{
    /// <summary>
    /// The calls of one turn whose results went back to the function-invocation loop but are in no response message
    /// yet. Once a call of the turn is carried (<see cref="ConversationToolRuns.Carry"/>), the cancel ends its round
    /// before the round writes its response messages, so no turn would read these results: they are kept as the carried
    /// call's is. Guarded by the lock of its <see cref="ConversationToolRuns"/>.
    /// </summary>
    internal sealed class ReturnedToolCalls
    {
        private readonly Dictionary<string, Func<IReadOnlyList<ChatMessage>>> _returned = new(StringComparer.Ordinal);

        private bool _broken;

        /// <summary>Notes a call that returned its result.</summary>
        /// <returns><see langword="true"/> when a call of the turn was carried already, so the pair is kept now.</returns>
        internal bool Add(string callId, Func<IReadOnlyList<ChatMessage>> pair)
        {
            if (_broken)
            {
                return true;
            }

            _returned[callId] = pair;
            return false;
        }

        /// <summary>Forgets a call whose result a response message carries.</summary>
        internal void Delivered(string callId)
        {
            _ = _returned.Remove(callId);
        }

        /// <summary>Marks the turn as ended by the cancel.</summary>
        /// <returns>The pairs of the calls that returned and reached no response message.</returns>
        internal List<Func<IReadOnlyList<ChatMessage>>> Break()
        {
            _broken = true;
            List<Func<IReadOnlyList<ChatMessage>>> taken = [.. _returned.Values];
            _returned.Clear();
            return taken;
        }
    }
}

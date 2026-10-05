using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Runtime.ToolCalls
{
    /// <summary>
    /// Stores the call and result of a tool a cut or withdrawn turn left running, as rows of the conversation, the
    /// moment the tool finished: they survive a reload, and a refused next turn, and every later turn reads them as
    /// history. A turn that holds the slot already read the history its request carries, so the rows wait for it to
    /// free the slot and land behind its own rows.
    /// </summary>
    /// <param name="session">The conversation.</param>
    internal sealed class FinishedToolPairs(ConversationSession session)
    {
        // Guarded by the session's turn lock.
        private readonly List<ChatMessage> _waiting = [];

        /// <summary>Stores one finished call and its result, now or after the running turn.</summary>
        internal void Keep(IReadOnlyList<ChatMessage> pair)
        {
            lock (session.TurnLock)
            {
                if (session.Cuts.RunningTurnIndex() is not null)
                {
                    _waiting.AddRange(pair);
                    return;
                }

                Write(pair);
            }
        }

        /// <summary>Stores what waited on the turn now freeing its slot. The cut tracker calls this under the turn lock.</summary>
        internal void AfterTurn()
        {
            if (_waiting.Count == 0)
            {
                return;
            }

            List<ChatMessage> waiting = [.. _waiting];
            _waiting.Clear();
            Write(waiting);
        }

        // The rows take the turn the conversation takes next, as a row a host appends between turns does.
        private void Write(IReadOnlyList<ChatMessage> messages)
        {
            if (session.Ledger.Session() is { } opened)
            {
                session.History.AppendBetweenTurns(opened, messages, session.State.TurnIndex);
            }
        }
    }
}

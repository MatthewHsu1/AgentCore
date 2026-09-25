using System.Runtime.CompilerServices;
using AgentCore.Application.Ports;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Transcript
{
    /// <summary>
    /// Finds the gate of each session and runs work against it alone. The provider is one object shared by
    /// every conversation, so this is what keeps two sessions on two transcripts.
    /// </summary>
    /// <remarks>Creates the registry over one backing store.</remarks>
    /// <param name="store">Store 1, which every gate writes through to.</param>
    /// <param name="logger">Where a refused write is logged.</param>
    internal sealed class ConversationGates(IConversationStore store, ILogger logger)
    {
        private readonly ConditionalWeakTable<AgentSession, ConversationGate> _gates = [];

        private readonly IConversationStore _store = store;

        private readonly ILogger _logger = logger;

        /// <summary>Runs one piece of work against the session's transcript, under the session's lock.</summary>
        public TResult Under<TResult>(AgentSession session, Func<ConversationTranscript, ConversationGate, TResult> work)
        {
            ConversationGate gate = GateFor(session);

            lock (gate.Sync)
            {
                return work(gate.Transcript, gate);
            }
        }

        /// <summary>Reads the tail of the session's store writes. It never faults.</summary>
        public Task Writes(AgentSession session)
        {
            ConversationGate gate = GateFor(session);
            lock (gate.Sync)
            {
                return gate.Writes;
            }
        }

        private ConversationGate GateFor(AgentSession session)
        {
            return _gates.GetValue(session, _ => new ConversationGate(_store, _logger));
        }
    }
}

using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>One turn as the runner files it: the invocation, its completer and its cut slot.</summary>
    internal sealed class HarnessTurn
    {
        private HarnessTurn(TurnInvocation invocation, RecordingTurnCompleter completer, TurnCutSlot slot)
        {
            Invocation = invocation;
            Completer = completer;
            Slot = slot;
        }

        public TurnInvocation Invocation { get; }

        public RecordingTurnCompleter Completer { get; }

        public TurnCutSlot Slot { get; }

        public List<AgentResponseUpdate> Updates { get; set; } = [];

        public static HarnessTurn Open(
            AgentCoreChatHistoryProvider history,
            AgentSession historySession,
            string text,
            int turnIndex,
            bool carriesHistory,
            List<string> log,
            ConversationTurnOrigin? origin = null,
            CancellationTokenSource? host = null)
        {
            RecordingTurnCompleter completer = new(history, historySession, log);
            TurnCutSlot slot = new(new Lock(), host?.Token ?? TestContext.Current.CancellationToken);
            TurnInvocation invocation = new()
            {
                ConversationId = ConversationTurnAgentHarness.ConversationId,
                TurnIndex = turnIndex,
                Stage = string.Empty,
                CarriesHistory = carriesHistory,
                HistorySession = historySession,
                User = new ChatMessage(ChatRole.User, text),
                Origin = origin,
                Completer = completer,
                CutSlot = slot,
            };
            return new HarnessTurn(invocation, completer, slot);
        }
    }
}

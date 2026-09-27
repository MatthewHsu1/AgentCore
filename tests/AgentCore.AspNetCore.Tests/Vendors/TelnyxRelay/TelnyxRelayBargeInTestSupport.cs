using AgentCore.Application.Runtime;
using AgentCore.Domain;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay
{
    /// <summary>What the barge-in test classes share: polling for the turn an interrupt settles.</summary>
    internal static class TelnyxRelayBargeInTestSupport
    {
        /// <summary>Polls for the turn a barge-in ends, bounded the same way <c>TelnyxRelayHost</c>'s own waits are.</summary>
        internal static async Task<TurnResult> WaitForTurnAsync(ConversationSession session)
        {
            for (int attempt = 0; attempt < 1_000; attempt++)
            {
                if (session.LastTurn is { } turn)
                {
                    return turn;
                }

                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            throw new TimeoutException("the interrupted turn never finished within ten seconds.");
        }
    }
}

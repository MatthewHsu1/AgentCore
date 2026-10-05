using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Notices;
using AgentCore.TestSupport;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>Scopes, notices and a wait the engine tests share.</summary>
    internal static class NoticeProbes
    {
        internal static HookScope Scope(string? conversationId = "c1", int? turn = 0, Guid? session = null) =>
            new(conversationId, "main", turn, "", session ?? Guid.Empty, 0, DateTimeOffset.UnixEpoch);

        internal static TurnStarted Started(int turn = 0, Guid? session = null, string conversationId = "c1") =>
            new(Scope(conversationId, turn, session), "only", "hi");

        /// <summary>
        /// Waits for a state no event announces, such as a fast hook's second delivery. It yields between checks
        /// and gives up after 5 s; it never waits on a clock.
        /// </summary>
        [AssertionMethod]
        internal static async Task WaitUntilAsync(Func<bool> condition)
        {
            using CancellationTokenSource limit = new(TimeSpan.FromSeconds(5));
            while (!condition())
            {
                await Task.Yield();
                limit.Token.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>Throws on every notice it overrides, the fault notice included.</summary>
    internal sealed class Thrower : AgentHook
    {
        public override ValueTask OnTurnStartedAsync(TurnStarted notice, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("thrower");

        public override ValueTask OnConversationEndedAsync(ConversationEnded notice, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("thrower on end");

        public override ValueTask OnFaultAsync(Fault notice, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("thrower on fault");
    }
}

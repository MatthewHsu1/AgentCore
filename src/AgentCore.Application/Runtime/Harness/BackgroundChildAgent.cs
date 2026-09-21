using AgentCore.Application.Runtime.Turn;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime.Harness
{
    /// <summary>
    /// Wraps one <c>background:</c> child so every session it gets knows which conversation started it,
    /// which zone the person on that call is in, and which workspace and shells that call owns.
    /// </summary>
    internal sealed class BackgroundChildAgent(AIAgent inner) : DelegatingAIAgent(inner)
    {
        /// <inheritdoc />
        protected override async ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        {
            AgentRunContext? parent = CurrentRunContext;

            AgentSession session = await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

            TurnInvocation? turn = TurnInvocation.From(parent?.RunOptions);

            if (turn?.ConversationId is { } conversationId)
            {
                session.StateBag.SetValue(BlobOwnerKey.Value, conversationId);
            }

            if (turn?.TimeZone is { } zone)
            {
                CallerTimeZone.Stamp(session, zone);
            }

            if (turn is not null)
            {
                TurnRegistry.Set(session, Shared(turn));
            }

            return session;
        }

        /// <summary>
        /// The part of the parent's turn a child may share: the conversation, its workspace folder, and its
        /// shells, so <c>files:</c> and <c>shell:</c> on the child work on the same disk. Every drain,
        /// tool list, and clarification stays with the parent; a child's publish reports a
        /// link in its result instead.
        /// </summary>
        private static TurnInvocation Shared(TurnInvocation parent)
        {
            return new()
            {
                ConversationId = parent.ConversationId,
                TurnIndex = parent.TurnIndex,
                Stage = parent.Stage,
                Workspace = parent.Workspace,
                Shells = parent.Shells,
                Knowledge = parent.Knowledge,
                TimeZone = parent.TimeZone,
                Nested = true,
            };
        }
    }
}

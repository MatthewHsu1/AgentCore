using AgentCore.Application.Runtime.Turn;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime.Harness
{
    /// <summary>
    /// Wraps one <c>background:</c> child so every session it gets knows which conversation started it,
    /// which zone the person on that call is in, and which workspace and shells that call owns.
    /// </summary>
    /// <remarks>
    /// <c>BackgroundAgentsProvider</c> creates the child session inside the parent's tool call, before
    /// it hands the run to a task. At that moment <see cref="AIAgent.CurrentRunContext"/> is still the
    /// parent's run, and its options carry the parent's turn. That is the only place the conversation id can
    /// cross from parent to child: the provider passes the child null run options and a fresh session,
    /// and its task-to-session map is private. Proven by probe P2 against Microsoft.Agents.AI 1.21.0.
    /// </remarks>
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
        /// screen, tool list, and clarification stays with the parent; a child's publish reports a
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

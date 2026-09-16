using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime.Harness;

/// <summary>
/// Wraps one <c>background:</c> child so every session it gets knows which call started it.
/// </summary>
/// <remarks>
/// <c>BackgroundAgentsProvider</c> creates the child session inside the parent's tool call, before
/// it hands the run to a task. At that moment <see cref="AIAgent.CurrentRunContext"/> is still the
/// parent's run, and its options carry the parent's turn. That is the only place the call id can
/// cross from parent to child: the provider passes the child null run options and a fresh session,
/// and its task-to-session map is private. Proven by probe P2 against Microsoft.Agents.AI 1.21.0.
/// </remarks>
internal sealed class BackgroundChildAgent(AIAgent inner) : DelegatingAIAgent(inner)
{
    /// <inheritdoc />
    protected override async ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        var parent = AIAgent.CurrentRunContext;

        var session = await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

        if (TurnInvocation.From(parent?.RunOptions)?.CallId is { } callId)
        {
            session.StateBag.SetValue(BlobOwnerKey.Value, callId);
        }

        return session;
    }
}

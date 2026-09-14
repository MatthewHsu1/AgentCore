using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Configuration.Compilation;

internal sealed class GraphStateEntry : ChatProtocolExecutor
{
    /// <summary>The workflow scope the snapshot is filed under. Named, because the default
    /// scope belongs to each executor alone and a gate would read its own empty one.</summary>
    internal const string StateScope = "graph-state";

    /// <summary>The workflow state key the snapshot is filed under.</summary>
    internal const string SlotsKey = "slots";

    /// <summary>Creates the entry of one compiled graph.</summary>
    internal GraphStateEntry()
        : base("agentcore-graph-entry", new ChatProtocolExecutorOptions { AutoSendTurnToken = true }, true)
    {
    }

    /// <inheritdoc />
    protected override async ValueTask TakeTurnAsync(
        List<ChatMessage> messages,
        IWorkflowContext context,
        bool? emitEvents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(context);

        if (GraphStateCarrier.TryTake(messages, out var snapshot) && snapshot is not null)
        {
            await context.QueueStateUpdateAsync(SlotsKey, snapshot, StateScope, cancellationToken)
                .ConfigureAwait(false);
        }

        await context.SendMessageAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

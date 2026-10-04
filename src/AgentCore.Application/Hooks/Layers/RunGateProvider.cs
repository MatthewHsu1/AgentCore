using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Runtime.Turn;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Hooks.Layers
{
    /// <summary>Once per run, when its context is built. Everything it adds is seen on every round.</summary>
    internal sealed class RunGateProvider(HookRuntime hooks) : AIContextProvider
    {
        private sealed record Added(IReadOnlyList<string> Instructions, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<AITool> Tools);

        protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            // The turn's own run is filed under its session, and a graph participant under its own with a nested
            // copy; an agent-as-tool child is found through the run's options and is nested too.
            TurnInvocation? filed = TurnRegistry.For(context.Session);
            TurnInvocation? turn = filed ?? TurnInvocation.From(AIAgent.CurrentRunContext?.RunOptions);
            if (turn?.Hooks is not { } raiser)
            {
                return new AIContext();
            }

            bool nested = turn.Nested || filed is null;
            HookScope scope = raiser.Scope(turn.TurnIndex, turn.Stage);
            IDictionary<string, object?> items = turn.Items ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            string agentId = context.Agent.Name ?? context.Agent.Id;

            Added added = await hooks.Gates.RunAsync(
                GatePoint.BeforeRun,
                scope,
                new Added([], [], []),
                _ => new RunGate(scope, agentId, nested, items),
                static (hook, gate, token) => hook.BeforeRunAsync(gate, token),
                static (state, gate) => new Added([.. state.Instructions, .. gate.Instructions], [.. state.Messages, .. gate.Messages], [.. state.Tools, .. gate.Tools]),
                static state => state,
                raiser.RaiseFault,
                cancellationToken).ConfigureAwait(false);

            return new AIContext
            {
                Instructions = added.Instructions.Count == 0 ? null : string.Join("\n\n", added.Instructions),
                Messages = added.Messages.Count == 0 ? null : added.Messages,
                Tools = added.Tools.Count == 0 ? null : added.Tools,
            };
        }
    }
}

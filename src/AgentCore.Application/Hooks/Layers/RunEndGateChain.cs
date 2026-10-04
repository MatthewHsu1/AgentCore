using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Hooks.Layers
{
    /// <summary>The run has a reply, before the turn is sealed. The loop evaluator in <c>ApplyLoop</c> calls this.</summary>
    internal static class RunEndGateChain
    {
        /// <summary>The run cap when the agent declares no <c>loop:</c>.</summary>
        internal const int DefaultMaxIterations = 3;

        /// <summary>Runs the chain and returns the message to run again with, or <see langword="null"/> to stop.</summary>
        /// <param name="hooks">The compiled hooks.</param>
        /// <param name="turn">The turn the run belongs to, or <see langword="null"/> outside a conversation turn.</param>
        /// <param name="agentId">The agent that ran, when known.</param>
        /// <param name="replyText">The run's reply text.</param>
        /// <param name="iteration">How many runs of this turn have completed, starting at 1.</param>
        /// <param name="cancellationToken">The turn's token.</param>
        internal static async ValueTask<string?> ContinueAsync(
            HookRuntime hooks, TurnInvocation? turn, string? agentId, string replyText, int iteration, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(hooks);

            if (turn is not { Nested: false, Hooks: { } raiser })
            {
                return null;
            }

            HookScope scope = raiser.Scope(turn.TurnIndex, turn.Stage);
            IDictionary<string, object?> items = turn.Items ?? new Dictionary<string, object?>(StringComparer.Ordinal);

            return await hooks.Gates.RunAsync<RunEndGate, string?>(
                GatePoint.AfterRun,
                scope,
                null,
                _ => new RunEndGate(scope, agentId, replyText, iteration, items),
                static (hook, gate, token) => hook.AfterRunAsync(gate, token),
                static (state, gate) => gate.Continuation ?? state,
                static state => state,
                raiser.RaiseFault,
                cancellationToken).ConfigureAwait(false);
        }
    }
}

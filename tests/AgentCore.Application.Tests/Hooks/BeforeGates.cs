using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>A hook that hands each of the four before-gates to an action, when one is given.</summary>
    internal sealed class BeforeGates(
        Action<TurnGate>? turn = null, Action<RunGate>? run = null, Action<ModelGate>? model = null, Action<ToolGate>? tool = null) : AgentHook
    {
        public override ValueTask BeforeTurnAsync(TurnGate gate, CancellationToken cancellationToken)
        {
            turn?.Invoke(gate);
            return default;
        }

        public override ValueTask BeforeRunAsync(RunGate gate, CancellationToken cancellationToken)
        {
            run?.Invoke(gate);
            return default;
        }

        public override ValueTask BeforeModelAsync(ModelGate gate, CancellationToken cancellationToken)
        {
            model?.Invoke(gate);
            return default;
        }

        public override ValueTask BeforeToolAsync(ToolGate gate, CancellationToken cancellationToken)
        {
            tool?.Invoke(gate);
            return default;
        }
    }
}

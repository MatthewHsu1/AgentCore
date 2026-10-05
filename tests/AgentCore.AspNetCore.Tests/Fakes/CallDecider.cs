using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>A host hook that decides every offered call with one action.</summary>
    internal sealed class CallDecider(Action<CallGate> decide) : AgentHook
    {
        public override ValueTask BeforeCallAsync(CallGate gate, CancellationToken cancellationToken)
        {
            decide(gate);
            return default;
        }
    }
}

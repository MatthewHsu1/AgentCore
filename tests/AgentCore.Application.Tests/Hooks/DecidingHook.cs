using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>An approval hook that decides each call with the given action and keeps every call id it was asked about.</summary>
    internal sealed class DecidingHook(Action<ApprovalGate> decide) : AgentHook
    {
        private readonly List<string> _asked = [];

        /// <summary>Gets the call id of every time the hook ran, in order.</summary>
        public IReadOnlyList<string> Asked
        {
            get
            {
                lock (_asked)
                {
                    return [.. _asked];
                }
            }
        }

        public override ValueTask BeforeToolApprovalAsync(ApprovalGate gate, CancellationToken cancellationToken)
        {
            lock (_asked)
            {
                _asked.Add(gate.CallId);
            }

            decide(gate);
            return default;
        }
    }
}

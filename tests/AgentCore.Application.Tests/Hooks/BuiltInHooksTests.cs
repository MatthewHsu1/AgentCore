using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.BuiltIn;
using Xunit;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class BuiltInHooksTests
    {
        // Built-ins run in this order: telemetry, then logging, then the audit chain.
        [Fact]
        public void TheBuiltInsRunInTheOrderTheObserversDid()
        {
            IReadOnlyList<AgentHook> hooks = BuiltInHooks.Create(new InMemoryAuditSink());

            Assert.Equal([typeof(TelemetryHook), typeof(LoggingHook), typeof(AuditHook)], hooks.Select(hook => hook.GetType()));
        }
    }
}

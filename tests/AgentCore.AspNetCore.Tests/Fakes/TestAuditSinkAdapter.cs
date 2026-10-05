using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>An audit vendor a test registers, which opens one store the test already holds.</summary>
    internal sealed class TestAuditSinkAdapter(IAuditSinkPort store) : IAuditSinkAdapter
    {
        public string Kind => "test";

        public ValueTask<IAuditSinkPort> OpenAsync(
            VendorProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(store);
        }
    }
}

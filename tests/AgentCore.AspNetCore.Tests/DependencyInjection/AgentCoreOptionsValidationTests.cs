using AgentCore.AspNetCore.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using static AgentCore.AspNetCore.Tests.DependencyInjection.StartedHostFixture;

namespace AgentCore.AspNetCore.Tests.DependencyInjection
{
    /// <summary>
    /// <see cref="AgentCoreOptions"/> validated eagerly on <c>host.StartAsync</c>, before
    /// <c>AgentCoreBootService</c> opens a single adapter.
    /// </summary>
    public sealed class AgentCoreOptionsValidationTests
    {
        [Fact]
        public async Task ANegativeResponseRetention_FailsTheHostStart()
        {
            OptionsValidationException failure = await Assert.ThrowsAsync<OptionsValidationException>(
                () => BuildAsync(OneAgentYaml, options => options.ResponseRetention = TimeSpan.FromSeconds(-1)));

            Assert.Contains("ResponseRetention", failure.Message, StringComparison.Ordinal);
        }
    }
}

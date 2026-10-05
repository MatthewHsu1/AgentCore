using System.Diagnostics.Metrics;
using Xunit;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>The collection <see cref="TelemetryHookTests"/> runs in, alone, because a <see cref="MeterListener"/> hears every meter in the process.</summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class TelemetryHookSuite
    {
        /// <summary>The name both the definition and the test class name it by.</summary>
        public const string Name = "telemetry hook";
    }
}

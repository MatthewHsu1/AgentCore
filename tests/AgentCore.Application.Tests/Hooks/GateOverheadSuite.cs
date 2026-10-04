using Xunit;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>The collection <see cref="GateOverheadTests"/> runs in, alone, so no other test shares its clock time.</summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class GateOverheadSuite
    {
        /// <summary>The name both the definition and the test class name it by.</summary>
        public const string Name = "gate overhead";
    }
}

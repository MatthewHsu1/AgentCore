using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>
    /// The collection the latency tests run in, alone.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class ConversationTurnLatencySuite
    {
        /// <summary>The name both the definition and the test class name it by.</summary>
        public const string Name = "conversation turn latency";
    }
}

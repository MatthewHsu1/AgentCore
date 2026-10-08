using AgentCore.Application.Conversation.Commands;
using Xunit;

namespace AgentCore.Application.Tests.Conversation
{
    /// <summary>
    /// A plan replaces the plan before it, so it is refused whole rather than cut: a cut plan loses its end, which
    /// usually says when to delegate again. One GPT-Live append holds 500 tokens.
    /// </summary>
    public sealed class SetVoicePlanCommandTests
    {
        [Fact]
        public void APlanAtTheLimitIsKeptWhole()
        {
            string plan = new('a', 1000);

            Assert.Equal(plan, new SetVoicePlanCommand(plan).Text);
        }

        [Fact]
        public void APlanOverTheLimitIsRefusedWithItsLengthAndTheLimit()
        {
            ArgumentException refused = Assert.Throws<ArgumentException>(() => new SetVoicePlanCommand(new string('a', 1001)));

            Assert.Contains("1001", refused.Message, StringComparison.Ordinal);
            Assert.Contains("1000", refused.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AWhiteSpacePlanIsRefused()
        {
            _ = Assert.Throws<ArgumentException>(() => new SetVoicePlanCommand("   "));
        }
    }
}

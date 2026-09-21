using AgentCore.Infrastructure.Llm.OpenAI;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Llm
{
    /// <summary>The built-in OpenAI context window table.</summary>
    public sealed class OpenAiContextWindowsTests
    {
        [Fact]
        public void Lookup_ExactId_ReturnsItsValue()
        {
            Assert.Equal(1_050_000, OpenAiContextWindows.Lookup("gpt-6-astra"));
        }

        [Fact]
        public void Lookup_DatedSnapshotId_ResolvesThroughTheBaseId()
        {
            Assert.Equal(1_047_576, OpenAiContextWindows.Lookup("gpt-4.1-mini-2025-04-14"));
        }

        [Fact]
        public void Lookup_LongerPrefixWins_OverAShorterPrefixThatAlsoMatches()
        {
            // "gpt-5.4" and "gpt-5.4-nano" are both entries, and both match "gpt-5.4-nano-2026-03-17".
            // The two have different windows, so this proves the longer prefix wins rather than the
            // first or shortest one.
            Assert.NotEqual(OpenAiContextWindows.Lookup("gpt-5.4"), OpenAiContextWindows.Lookup("gpt-5.4-nano"));
            Assert.Equal(400_000, OpenAiContextWindows.Lookup("gpt-5.4-nano-2026-03-17"));
        }

        [Fact]
        public void Lookup_UnknownId_ReturnsNull()
        {
            Assert.Null(OpenAiContextWindows.Lookup("gpt-does-not-exist"));
        }
    }
}

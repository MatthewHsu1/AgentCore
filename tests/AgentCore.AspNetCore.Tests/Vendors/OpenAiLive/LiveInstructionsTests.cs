using AgentCore.AspNetCore.Vendors.OpenAiLive.Call;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>The strict paragraph is always added; the brief is appended verbatim.</summary>
    public sealed class LiveInstructionsTests
    {
        // The paragraph that made GPT-Live delegate in 6 of 6 probe runs, word for word.
        private const string Strict =
            "You know no product facts yourself. For any question about products, specifications, orders,"
            + " parts, or policies, delegate to the backend and wait for its answer. Never guess a fact."
            + " You cannot end the call yourself: when the caller wants to end or hang up the call, delegate that"
            + " to the backend, and say goodbye with its answer.";

        [Fact]
        public void TheConfiguredPromptAlwaysGetsTheStrictParagraph()
        {
            Assert.Equal("You are a phone support agent. Be brief.\n\n" + Strict, LiveInstructions.Build("You are a phone support agent. Be brief.\n", brief: null));
        }

        [Fact]
        public void TheBriefIsAppendedExactlyAsTheHostWroteIt()
        {
            const string brief = "Earlier summary, for context only.\n  \"Ask first.\" ";

            Assert.Equal("Be brief.\n\n" + Strict + "\n\n" + brief, LiveInstructions.Build("Be brief.", brief));
        }
    }
}

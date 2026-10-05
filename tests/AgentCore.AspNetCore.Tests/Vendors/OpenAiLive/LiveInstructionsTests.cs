using AgentCore.AspNetCore.Vendors.OpenAiLive.Call;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>The configured prompt goes out as written; the brief is appended verbatim.</summary>
    public sealed class LiveInstructionsTests
    {
        [Fact]
        public void WithNoBriefTheConfiguredPromptIsSentAsWritten()
        {
            Assert.Equal("You are a phone support agent. Be brief.\n", LiveInstructions.Build("You are a phone support agent. Be brief.\n", brief: null));
        }

        [Fact]
        public void TheBriefIsAppendedExactlyAsTheHostWroteIt()
        {
            const string brief = "Earlier summary, for context only.\n  \"Ask first.\" ";

            Assert.Equal("Be brief.\n\n" + brief, LiveInstructions.Build("Be brief.\n", brief));
        }
    }
}

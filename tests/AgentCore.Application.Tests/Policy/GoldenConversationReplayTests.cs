using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Policy;
using AgentCore.Application.Tests.Fakes;
using Xunit;

namespace AgentCore.Application.Tests.Policy
{
    /// <summary>
    /// Rule 15 of section 11. The six golden conversations of <c>spike/callpolicy</c> replay against a
    /// configuration-declared policy and reproduce the hand-written machine exactly.
    /// </summary>
    public sealed class GoldenConversationReplayTests
    {
        private static readonly AgentCoreConfiguration Document = ConfigurationLoader.LoadYaml(GoldenSet.Yaml);

        public static TheoryData<string> ConversationNames()
        {
            TheoryData<string> names = [];
            foreach (GoldenConversation conversation in GoldenSet.Conversations)
            {
                names.Add(conversation.Name);
            }

            return names;
        }

        [Fact]
        public void GoldenSet_HoldsSixConversations()
        {
            Assert.Equal(6, GoldenSet.Conversations.Count);
        }

        [Theory]
        [MemberData(nameof(ConversationNames))]
        public void GoldenConversation_ReplaysThroughTheDeclaredPolicyExactly(string name)
        {
            GoldenConversation conversation = GoldenSet.Conversations.Single(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));

            List<string> expected = [.. GoldenSet.ReplayHandWritten(conversation).Select(Transitions.ToStageId)];
            List<string> actual = ReplayDeclared(conversation);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void EveryGoldenConversation_ReplaysThroughTheDeclaredPolicyExactly()
        {
            foreach (GoldenConversation conversation in GoldenSet.Conversations)
            {
                List<string> expected = [.. GoldenSet.ReplayHandWritten(conversation).Select(Transitions.ToStageId)];
                Assert.Equal(expected, ReplayDeclared(conversation));
            }
        }

        [Fact]
        public void HumanRequest_BeatsAResolvedFix()
        {
            GoldenConversation conversation = GoldenSet.Conversations.Single(candidate =>
                string.Equals(candidate.Name, "human-request-beats-a-resolved-fix", StringComparison.Ordinal));

            // Both resolved and callerAskedForHuman are true on the last turn, and the answer is escalate.
            Assert.Equal("escalate", ReplayDeclared(conversation)[^1]);
        }

        [Fact]
        public void DeclaredPolicy_RendersEachGuardNameNextToItsEdge()
        {
            StagePolicy policy = new(Document.Entries["main"].Policy!, new TestGuardEvaluator(Document));

            string mermaid = policy.ToMermaid();

            // Section 8.4: a name renders, and an inline JSONLogic tree renders as an unreadable blob.
            Assert.Contains("wantsHuman", mermaid, StringComparison.Ordinal);
            Assert.Contains("humanOrExhausted", mermaid, StringComparison.Ordinal);
            Assert.DoesNotContain("callerSaidGoodbye", mermaid, StringComparison.Ordinal);
        }

        private static List<string> ReplayDeclared(GoldenConversation conversation)
        {
            StagePolicy policy = new(Document.Entries["main"].Policy!, new TestGuardEvaluator(Document));

            List<string> stages = new(conversation.Turns.Count);
            for (int index = 0; index < conversation.Turns.Count; index++)
            {
                IReadOnlyDictionary<string, JsonNode?> snapshot = conversation.Turns[index].Facts.ToSnapshot(policy.Stage, index);
                stages.Add(policy.Advance(snapshot));
            }

            return stages;
        }
    }
}

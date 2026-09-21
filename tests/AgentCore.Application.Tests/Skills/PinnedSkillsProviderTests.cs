using AgentCore.Application.Skills;
using AgentCore.Application.Tests.Runtime;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentCore.Application.Tests.Skills
{
    /// <summary>
    /// A pinned skill's body must reach the prompt on every turn without a tool call, with the
    /// discovery frontmatter stripped, and in the order the agent listed it.
    /// </summary>
    public sealed class PinnedSkillsProviderTests
    {
        [Fact]
        public async Task Invoke_PutsTheBodyInTheInstructionsAndRegistersNoTool()
        {
            AIContext result = await InvokeAsync(["warranty-returns"]);

            Assert.Equal("<skill name=\"warranty-returns\">\nDo the thing.\n</skill>", result.Instructions);
            Assert.Null(result.Tools);
        }

        [Fact]
        public async Task Invoke_StripsTheFrontmatter()
        {
            AIContext result = await InvokeAsync(["warranty-returns"]);

            Assert.DoesNotContain("---", result.Instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("description:", result.Instructions, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Invoke_TwoSkills_KeepsTheListedOrder()
        {
            AIContext result = await InvokeAsync(["shipping-claims", "warranty-returns"]);

            int shipping = result.Instructions!.IndexOf("name=\"shipping-claims\"", StringComparison.Ordinal);
            int warranty = result.Instructions.IndexOf("name=\"warranty-returns\"", StringComparison.Ordinal);
            Assert.True(shipping >= 0 && warranty > shipping);
        }

        [Fact]
        public async Task Invoke_AppendsBelowTheInstructionsAlreadyThere()
        {
            using SkillFolder folder = SkillFolder.Create().WithSkill("warranty-returns");
            using AgentFileSkillsSource source = new(folder.Root);
            SkillCatalog catalog = new(source, new HashSet<string>(["warranty-returns"], StringComparer.Ordinal));
            PinnedSkillsProvider provider = new(catalog, ["warranty-returns"]);

            AIContext result = await InvokeAsync(provider, new AIContext { Instructions = "You are support." });

            Assert.StartsWith("You are support.", result.Instructions, StringComparison.Ordinal);
            Assert.EndsWith("Do the thing.\n</skill>", result.Instructions, StringComparison.Ordinal);
        }

        [Fact]
        public void Constructor_NoNames_Throws()
        {
            using SkillFolder folder = SkillFolder.Create().WithSkill("warranty-returns");
            using AgentFileSkillsSource source = new(folder.Root);
            SkillCatalog catalog = new(source, new HashSet<string>(["warranty-returns"], StringComparer.Ordinal));

            _ = Assert.Throws<ArgumentException>(() => new PinnedSkillsProvider(catalog, []));
        }

        private static async Task<AIContext> InvokeAsync(IReadOnlyList<string> pinned)
        {
            using SkillFolder folder = SkillFolder.Create()
                .WithSkill("warranty-returns")
                .WithSkill("shipping-claims");

            using AgentFileSkillsSource source = new(folder.Root);
            SkillCatalog catalog = new(source, new HashSet<string>(["warranty-returns", "shipping-claims"], StringComparer.Ordinal));

            return await InvokeAsync(new PinnedSkillsProvider(catalog, pinned), new AIContext());
        }

        private static async Task<AIContext> InvokeAsync(AIContextProvider provider, AIContext existing)
        {
            using SequencedChatClient client = new("hello there.");
            ChatClientAgent agent = new(client, new ChatClientAgentOptions { Name = "support" });

#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
            AIContextProvider.InvokingContext context = new(agent, null, existing);
#pragma warning restore MAAI001
            return await provider.InvokingAsync(context, TestContext.Current.CancellationToken);
        }
    }
}

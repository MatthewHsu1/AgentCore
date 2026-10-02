using System.Text.Json;
using AgentCore.Application.Skills;
using AgentCore.Application.Tests.Runtime;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Skills
{
    /// <summary>
    /// One agent must see its own skills and no others, must never be asked to approve a tool call
    /// mid-conversation, must never be told a script tool exists, and must be pointed at a pinned
    /// skill's body when it tries to load one.
    /// </summary>
    public sealed class SkillsProviderFactoryTests
    {
        [Fact]
        public async Task Create_AdvertisesOnlyTheSkillsTheAgentListed()
        {
            AIContext result = await InvokeAsync(["warranty-returns"]);

            Assert.Contains("warranty-returns", result.Instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("shipping-claims", result.Instructions, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Create_RegistersTheTwoReadToolsAndNoScriptTool()
        {
            AIContext result = await InvokeAsync(["warranty-returns"]);

            Assert.NotNull(result.Tools);
            Assert.Equal(["load_skill", "read_skill_resource"], result.Tools.Select(tool => tool.Name));
        }

        [Fact]
        public async Task Create_TheReadToolsDoNotRequireApproval()
        {
            AIContext result = await InvokeAsync(["warranty-returns"]);

            Assert.DoesNotContain(result.Tools!, tool => tool is ApprovalRequiredAIFunction);
        }

        [Fact]
        public async Task Create_ThePromptNeverNamesTheScriptTool()
        {
            AIContext result = await InvokeAsync(["warranty-returns"]);

            Assert.DoesNotContain("run_skill_script", result.Instructions, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Create_TwoAgentsOverOneSource_BothKeepWorkingAfterOneIsDisposed()
        {
            using SkillFolder folder = SkillFolder.Create()
                .WithSkill("warranty-returns")
                .WithSkill("shipping-claims");

            using AgentFileSkillsSource source = new(folder.Root);
            using CachingAgentSkillsSource shared = new(source);
            SkillCatalog catalog = new(shared, new HashSet<string>(["warranty-returns", "shipping-claims"], StringComparer.Ordinal));

            AIContextProvider first = SkillsProviderFactory.Create(catalog, ["warranty-returns"], pinned: [], loggers: null);
            AIContextProvider second = SkillsProviderFactory.Create(catalog, ["shipping-claims"], pinned: [], loggers: null);

            (first as IDisposable)?.Dispose();

            AIContext result = await InvokeAsync(second);

            Assert.Contains("shipping-claims", result.Instructions, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LoadSkill_APinnedName_SaysTheBodyIsAlreadyInTheInstructions(bool asJson)
        {
            string? answer = await LoadSkillAsync("shipping-claims", asJson);

            Assert.Equal(
                "The 'shipping-claims' skill is already in your instructions, inside <skill name=\"shipping-claims\">. Use it from there.",
                answer);
        }

        [Fact]
        public async Task LoadSkill_ALoadableName_ReturnsItsBody()
        {
            string? answer = await LoadSkillAsync("warranty-returns", asJson: true);

            Assert.Contains("Do the thing.", answer, StringComparison.Ordinal);
        }

        [Fact]
        public async Task LoadSkill_AnUnknownName_KeepsTheFrameworksNotFoundAnswer()
        {
            string? answer = await LoadSkillAsync("returns", asJson: true);

            Assert.Equal("Error: Skill 'returns' not found.", answer);
        }

        /// <summary>
        /// Calls load_skill on an agent that may load warranty-returns and pins shipping-claims. A
        /// model's arguments arrive as <see cref="JsonElement"/>; a direct caller may pass a string.
        /// MAF hands its own answers back as a JSON string, so both shapes are read as text.
        /// </summary>
        private static async Task<string?> LoadSkillAsync(string skillName, bool asJson)
        {
            using SkillFolder folder = SkillFolder.Create()
                .WithSkill("warranty-returns")
                .WithSkill("shipping-claims");

            using AgentFileSkillsSource source = new(folder.Root);
            using CachingAgentSkillsSource shared = new(source);
            SkillCatalog catalog = new(shared, new HashSet<string>(["warranty-returns", "shipping-claims"], StringComparer.Ordinal));

            AIContext context = await InvokeAsync(SkillsProviderFactory.Create(catalog, ["warranty-returns"], pinned: ["shipping-claims"], loggers: null));
            AIFunction loadSkill = context.Tools!.OfType<AIFunction>().Single(tool => tool.Name == AgentSkillsProvider.LoadSkillToolName);

            AIFunctionArguments arguments = new()
            {
                ["skillName"] = asJson ? JsonSerializer.SerializeToElement(skillName) : skillName,
            };
            object? answer = await loadSkill.InvokeAsync(arguments, TestContext.Current.CancellationToken);
            return answer is JsonElement json ? json.GetString() : answer as string;
        }

        private static async Task<AIContext> InvokeAsync(IReadOnlyList<string> allowed)
        {
            using SkillFolder folder = SkillFolder.Create()
                .WithSkill("warranty-returns")
                .WithSkill("shipping-claims");

            using AgentFileSkillsSource source = new(folder.Root);
            using CachingAgentSkillsSource shared = new(source);
            SkillCatalog catalog = new(shared, new HashSet<string>(["warranty-returns", "shipping-claims"], StringComparer.Ordinal));

            return await InvokeAsync(SkillsProviderFactory.Create(catalog, allowed, pinned: [], loggers: null));
        }

        private static async Task<AIContext> InvokeAsync(AIContextProvider provider)
        {
            using SequencedChatClient client = new("hello there.");
            ChatClientAgent agent = new(client, new ChatClientAgentOptions { Name = "support" });

#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
            AIContextProvider.InvokingContext context = new(agent, null, new AIContext());
#pragma warning restore MAAI001
            return await provider.InvokingAsync(context, TestContext.Current.CancellationToken);
        }
    }
}

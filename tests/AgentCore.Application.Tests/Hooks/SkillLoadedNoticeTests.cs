using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Skills;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>
    /// A pinned skill is one <c>Pinned</c> notice per run; a <c>load_skill</c> that found its skill, and
    /// that the redirect did not answer, is one load.
    /// </summary>
    public sealed class SkillLoadedNoticeTests
    {
        private const string SkillsYaml = """
        apiVersion: agentcore/v1
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "ok", skills: [warranty-returns], pinned: [shipping-claims] }
        entries:
          main:
            agent: only
        """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task APinnedSkillAndASkillTheModelLoadsAreOneNoticeEach()
        {
            IReadOnlyList<(string, bool)> loaded = await LoadAsync("warranty-returns");

            Assert.Equal([("shipping-claims", true), ("warranty-returns", false)], loaded);
        }

        [Fact]
        public async Task LoadingAPinnedSkillLoadsNothingMore()
        {
            IReadOnlyList<(string, bool)> loaded = await LoadAsync("shipping-claims");

            Assert.Equal([("shipping-claims", true)], loaded);
        }

        // A load_skill MAF answered "not found" loaded nothing.
        [Fact]
        public async Task LoadingASkillThatDoesNotExistLoadsNothing()
        {
            IReadOnlyList<(string, bool)> loaded = await LoadAsync("no-such-skill");

            Assert.Equal([("shipping-claims", true)], loaded);
        }

        private static async Task<IReadOnlyList<(string, bool)>> LoadAsync(string skillName)
        {
            using SkillFolder folder = SkillFolder.Create()
                .WithSkill("warranty-returns")
                .WithSkill("shipping-claims");
            using AgentFileSkillsSource source = new(folder.Root);
            SkillCatalog catalog = new(source, new HashSet<string>(["warranty-returns", "shipping-claims"], StringComparer.Ordinal));

            RecordingHook hook = new();
            ToolCallingChatClient model = new("final", new Dictionary<string, object?>(StringComparer.Ordinal) { ["skillName"] = skillName });
            ConversationSession session = HookSessions.Create(SkillsYaml, model, [hook], skills: catalog);

            _ = await session.RunTurnAsync("how do returns work?", Ct);
            await session.FlushNoticesAsync();

            Assert.Equal([AgentSkillsProvider.LoadSkillToolName], model.Called);
            return [.. hook.Of<SkillLoaded>().Select(notice => (notice.Name, notice.Pinned))];
        }
    }
}

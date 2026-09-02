using AgentCore.Application.Skills;
using AgentCore.Application.Tests.Runtime;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Skills;

/// <summary>
/// One agent must see its own skills and no others, must never be asked to approve a tool call
/// mid-call, and must never be told a script tool exists.
/// </summary>
public sealed class SkillsProviderFactoryTests
{
    [Fact]
    public async Task Create_AdvertisesOnlyTheSkillsTheAgentListed()
    {
        var result = await InvokeAsync(["warranty-returns"]);

        Assert.Contains("warranty-returns", result.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("shipping-claims", result.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_RegistersTheTwoReadToolsAndNoScriptTool()
    {
        var result = await InvokeAsync(["warranty-returns"]);

        Assert.NotNull(result.Tools);
        Assert.Equal(["load_skill", "read_skill_resource"], result.Tools.Select(tool => tool.Name));
    }

    [Fact]
    public async Task Create_TheReadToolsDoNotRequireApproval()
    {
        var result = await InvokeAsync(["warranty-returns"]);

        Assert.DoesNotContain(result.Tools!, tool => tool is ApprovalRequiredAIFunction);
    }

    [Fact]
    public async Task Create_ThePromptNeverNamesTheScriptTool()
    {
        var result = await InvokeAsync(["warranty-returns"]);

        Assert.DoesNotContain("run_skill_script", result.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_TwoAgentsOverOneSource_BothKeepWorkingAfterOneIsDisposed()
    {
        using var folder = SkillFolder.Create()
            .WithSkill("warranty-returns")
            .WithSkill("shipping-claims");

        using AgentFileSkillsSource source = new(folder.Root);
        using CachingAgentSkillsSource shared = new(source);
        SkillCatalog catalog = new(shared, new HashSet<string>(["warranty-returns", "shipping-claims"], StringComparer.Ordinal));

        var first = SkillsProviderFactory.Create(catalog, ["warranty-returns"], loggers: null);
        var second = SkillsProviderFactory.Create(catalog, ["shipping-claims"], loggers: null);

        (first as IDisposable)?.Dispose();

        var result = await InvokeAsync(second);

        Assert.Contains("shipping-claims", result.Instructions, StringComparison.Ordinal);
    }

    private static async Task<AIContext> InvokeAsync(IReadOnlyList<string> allowed)
    {
        using var folder = SkillFolder.Create()
            .WithSkill("warranty-returns")
            .WithSkill("shipping-claims");

        using AgentFileSkillsSource source = new(folder.Root);
        using CachingAgentSkillsSource shared = new(source);
        SkillCatalog catalog = new(shared, new HashSet<string>(["warranty-returns", "shipping-claims"], StringComparer.Ordinal));

        return await InvokeAsync(SkillsProviderFactory.Create(catalog, allowed, loggers: null));
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

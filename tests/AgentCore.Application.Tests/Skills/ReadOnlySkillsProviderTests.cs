using AgentCore.Application.Skills;
using AgentCore.Application.Tests.Runtime;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Skills;

/// <summary>
/// The wrapper exists for one reason: MAF registers run_skill_script even when no skill has a
/// script, and this host runs none. It must remove that tool without disturbing anything the
/// caller already put in the context — overriding the wrong method silently doubles it.
/// </summary>
public sealed class ReadOnlySkillsProviderTests
{
    [Fact]
    public async Task InvokingAsync_RemovesTheScriptToolAndLeavesTheCallersContextIntact()
    {
        using var folder = SkillFolder.Create().WithSkill("warranty-returns");
        using AgentFileSkillsSource source = new(folder.Root);
        using AgentSkillsProvider inner = new(
            source,
            new AgentSkillsProviderOptions
            {
                DisableLoadSkillApproval = true,
                DisableReadSkillResourceApproval = true,
            },
            loggerFactory: null,
            ownsSource: false);

        using ReadOnlySkillsProvider provider = new(inner);

        using SequencedChatClient client = new("hello there.");
        ChatClientAgent agent = new(client, new ChatClientAgentOptions { Name = "support" });
        AIFunction callerTool = AIFunctionFactory.Create(() => "x", name: "caller_tool", description: "A tool the agent already had.");
        AIContext seed = new() { Tools = [callerTool], Instructions = "CALLER." };

#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        AIContextProvider.InvokingContext context = new(agent, null, seed);
#pragma warning restore MAAI001
        var result = await provider.InvokingAsync(context, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Tools);
        Assert.Equal(
            ["caller_tool", "load_skill", "read_skill_resource"],
            result.Tools.Select(tool => tool.Name));

        // Exactly once. Overriding ProvideAIContextAsync instead merges the caller's context twice.
        Assert.Equal(1, result.Instructions!.Split("CALLER.").Length - 1);
    }

    [Fact]
    public async Task InvokingAsync_WhenTheFilterMatchesNothing_ReturnsNoToolsAndDoesNotThrow()
    {
        using var folder = SkillFolder.Create().WithSkill("warranty-returns");
        using AgentFileSkillsSource source = new(folder.Root);
        using FilteringAgentSkillsSource filtered = new(source, (_, _) => false);
        using AgentSkillsProvider inner = new(filtered, options: null, loggerFactory: null, ownsSource: false);
        using ReadOnlySkillsProvider provider = new(inner);

        using SequencedChatClient client = new("hello there.");
        ChatClientAgent agent = new(client, new ChatClientAgentOptions { Name = "support" });

#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        AIContextProvider.InvokingContext context = new(agent, null, new AIContext());
#pragma warning restore MAAI001
        var result = await provider.InvokingAsync(context, TestContext.Current.CancellationToken);

        Assert.Null(result.Tools);
    }
}

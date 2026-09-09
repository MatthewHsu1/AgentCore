using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using Xunit;

namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// <see cref="AgentCompaction"/>: the key-by-key merge of one agent's <c>compaction:</c> block
/// against <c>agents.defaults.compaction</c>.
/// </summary>
public sealed class AgentCompactionTests
{
    [Fact]
    public void Compose_NoBlockAnywhere_IsNull()
    {
        Assert.Null(AgentCompaction.Compose(defaults: null, Agent(compaction: null)));
    }

    [Fact]
    public void Compose_DefaultsOnly_IsInherited()
    {
        var resolved = AgentCompaction.Compose(
            new AgentDefaults { Compaction = Block(CompactionStrategyKind.Truncate) },
            Agent(compaction: null));

        Assert.NotNull(resolved);
        Assert.Equal(CompactionStrategyKind.Truncate, resolved.Strategy);
    }

    [Fact]
    public void Compose_AgentOverridesDefaults_TakesTheAgent()
    {
        var resolved = AgentCompaction.Compose(
            new AgentDefaults { Compaction = Block(CompactionStrategyKind.Truncate) },
            Agent(Block(CompactionStrategyKind.ToolResult)));

        Assert.NotNull(resolved);
        Assert.Equal(CompactionStrategyKind.ToolResult, resolved.Strategy);
    }

    [Fact]
    public void Compose_UnsetKeys_TakeTheDocumentedDefaults()
    {
        var resolved = AgentCompaction.Compose(
            defaults: null,
            Agent(new CompactionConfiguration
            {
                Strategy = CompactionStrategyKind.ToolResult,
                Trigger = new CompactionTriggerConfiguration { Tokens = 60000 },
            }));

        Assert.NotNull(resolved);
        Assert.Equal(AgentCompaction.DefaultKeep, resolved.Keep);
        Assert.Equal(AgentCompaction.DefaultFoldResultChars, resolved.FoldResultChars);
    }

    [Fact]
    public void Compose_NoTrigger_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => AgentCompaction.Compose(
            defaults: null,
            Agent(new CompactionConfiguration { Strategy = CompactionStrategyKind.Truncate })));

        Assert.Contains("exactly one", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_TwoTriggers_Throws()
    {
        Assert.Throws<ArgumentException>(() => AgentCompaction.Compose(
            defaults: null,
            Agent(new CompactionConfiguration
            {
                Strategy = CompactionStrategyKind.Truncate,
                Trigger = new CompactionTriggerConfiguration { Tokens = 60000, Messages = 40 },
            })));
    }

    private static CompactionConfiguration Block(CompactionStrategyKind strategy) => new()
    {
        Strategy = strategy,
        Trigger = new CompactionTriggerConfiguration { Tokens = 60000 },
    };

    private static AgentConfiguration Agent(CompactionConfiguration? compaction) =>
        new() { Id = "reply", Compaction = compaction };
}

using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Agents.AI.Compaction;

namespace AgentCore.Application.Runtime.Compaction;

/// <summary>
/// Builds the MAF compaction strategy one agent's resolved <c>compaction:</c> block names.
/// </summary>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.17.0.
internal static class CompactionStrategyFactory
{
    /// <summary>Builds the strategy.</summary>
    /// <param name="settings">The agent's resolved block.</param>
    /// <returns>The strategy, ready for a <see cref="CompactionProvider"/>.</returns>
    public static CompactionStrategy Create(ResolvedCompaction settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var trigger = Trigger(settings.Trigger);

        return settings.Strategy switch
        {
            CompactionStrategyKind.Truncate => new TruncationCompactionStrategy(trigger, settings.Keep),
            CompactionStrategyKind.SlidingWindow => new SlidingWindowCompactionStrategy(trigger, settings.Keep),
            CompactionStrategyKind.ContextWindow => ContextWindow(settings),
            _ => ToolResult(settings, trigger),
        };
    }

    private static ToolResultCompactionStrategy ToolResult(ResolvedCompaction settings, CompactionTrigger trigger)
        => new(trigger, settings.Keep)
        {
            ToolCallFormatter = CappedToolCallFormatter.Create(settings.FoldResultChars),
        };

    private static PipelineCompactionStrategy ContextWindow(ResolvedCompaction settings)
        => new PipelineCompactionStrategy(
        [
            ToolResult(settings, Trigger(settings.Trigger)),
            new TruncationCompactionStrategy(Trigger(settings.Trigger), settings.Keep),
        ]);

    private static CompactionTrigger Trigger(CompactionTriggerConfiguration trigger) => trigger switch
    {
        { Tokens: { } tokens } => CompactionTriggers.TokensExceed(tokens),
        { Messages: { } messages } => CompactionTriggers.MessagesExceed(messages),
        { Turns: { } turns } => CompactionTriggers.TurnsExceed(turns),
        { Groups: { } groups } => CompactionTriggers.GroupsExceed(groups),
        _ => throw new ArgumentException(
            "the trigger names no condition. AgentCompaction.Compose refuses this before it gets here.",
            nameof(trigger)),
    };
}
#pragma warning restore MAAI001

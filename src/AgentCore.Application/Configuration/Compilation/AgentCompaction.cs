using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>One agent's <c>compaction:</c> block, fully resolved.</summary>
/// <param name="Strategy">The strategy to build.</param>
/// <param name="Trigger">The one condition that makes compaction run.</param>
/// <param name="Keep">How many newest groups the strategy never touches.</param>
/// <param name="FoldResultChars">How much of a tool result a fold carries forward.</param>
public sealed record ResolvedCompaction(
    CompactionStrategyKind Strategy,
    CompactionTriggerConfiguration Trigger,
    int Keep,
    int FoldResultChars);

/// <summary>
/// Composes one agent's <c>compaction:</c> block from <c>agents.defaults.compaction</c> and the
/// agent's own block, key by key.
/// </summary>
public static class AgentCompaction
{
    /// <summary>The strategy used when neither the agent nor the defaults name one.</summary>
    public const CompactionStrategyKind DefaultStrategy = CompactionStrategyKind.ToolResult;

    /// <summary>The newest groups a strategy never touches, when nothing names a number.</summary>
    public const int DefaultKeep = 4;

    /// <summary>
    /// How much of a tool result a fold carries forward, when nothing names a number. Enough for
    /// the model to recognise what a tool already returned, and not enough to refill the window.
    /// </summary>
    public const int DefaultFoldResultChars = 200;

    /// <summary>Composes one agent's resolved compaction settings.</summary>
    /// <param name="defaults">The <c>agents.defaults</c> section, or <see langword="null"/>.</param>
    /// <param name="agent">The agent to resolve.</param>
    /// <returns>
    /// The resolved settings, or <see langword="null"/> when neither the defaults nor the agent
    /// declares a <c>compaction:</c> block.
    /// </returns>
    /// <exception cref="ArgumentException">The composed trigger names other than one condition.</exception>
    public static ResolvedCompaction? Compose(AgentDefaults? defaults, AgentConfiguration agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var own = agent.Compaction;
        var shared = defaults?.Compaction;

        if (own is null && shared is null)
        {
            return null;
        }

        var trigger = own?.Trigger ?? shared?.Trigger ?? new CompactionTriggerConfiguration();

        var named = new int?[] { trigger.Tokens, trigger.Messages, trigger.Turns, trigger.Groups }
            .Count(value => value is not null);

        if (named != 1)
        {
            // Zero would compact on every turn; two would leave the document silent about whether
            // they are combined with All or Any.
            throw new ArgumentException(
                $"the agent '{agent.Id}' has a compaction trigger naming {named} conditions. It must "
                + "name exactly one of tokens, messages, turns or groups.",
                nameof(agent));
        }

        return new ResolvedCompaction(
            own?.Strategy ?? shared?.Strategy ?? DefaultStrategy,
            trigger,
            own?.Keep ?? shared?.Keep ?? DefaultKeep,
            own?.FoldResultChars ?? shared?.FoldResultChars ?? DefaultFoldResultChars);
    }
}

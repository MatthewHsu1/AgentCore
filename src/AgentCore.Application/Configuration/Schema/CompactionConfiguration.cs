namespace AgentCore.Application.Configuration.Schema;

/// <summary>The strategies a <c>compaction:</c> block may name.</summary>
public enum CompactionStrategyKind
{
    /// <summary>
    /// Folds old tool-call groups into one short line each. It folds only function tool calls — a
    /// group built from <see cref="Microsoft.Extensions.AI.FunctionCallContent"/> and its result. A
    /// provider-run tool, such as hosted web search, is not folded: its messages are not that
    /// content type, so this strategy leaves them untouched. Use
    /// <see cref="ContextWindow"/> for a conversation that mixes the two, since its truncate phase
    /// drops whole groups regardless of what is inside them.
    /// </summary>
    ToolResult,

    /// <summary>Removes the oldest non-system groups.</summary>
    Truncate,

    /// <summary>Removes the oldest user turns and everything that answered them.</summary>
    SlidingWindow,

    /// <summary>
    /// Folds tool results, then truncates. The name names the goal, not the mechanism: nothing here
    /// reads the model's actual context window. The size this strategy holds to comes entirely from
    /// this block's own <see cref="CompactionConfiguration.Trigger"/> and
    /// <see cref="CompactionConfiguration.Keep"/>.
    /// </summary>
    ContextWindow,
}

/// <summary>When compaction runs. Exactly one member is set.</summary>
public sealed record CompactionTriggerConfiguration
{
    /// <summary>Gets the token count above which compaction runs.</summary>
    public int? Tokens { get; init; }

    /// <summary>Gets the message count above which compaction runs.</summary>
    public int? Messages { get; init; }

    /// <summary>Gets the turn count above which compaction runs.</summary>
    public int? Turns { get; init; }

    /// <summary>Gets the group count above which compaction runs.</summary>
    public int? Groups { get; init; }
}

/// <summary>One agent's <c>compaction:</c> block, as the document writes it.</summary>
public sealed record CompactionConfiguration
{
    /// <summary>Gets the strategy, or <see langword="null"/> to inherit.</summary>
    public CompactionStrategyKind? Strategy { get; init; }

    /// <summary>Gets when compaction runs, or <see langword="null"/> to inherit.</summary>
    public CompactionTriggerConfiguration? Trigger { get; init; }

    /// <summary>Gets how many newest groups the strategy never touches, or null to inherit.</summary>
    public int? Keep { get; init; }

    /// <summary>
    /// Gets how many characters of each tool result a fold carries forward, or null to inherit.
    /// <see cref="CompactionStrategyKind.ToolResult"/> only.
    /// </summary>
    public int? FoldResultChars { get; init; }
}

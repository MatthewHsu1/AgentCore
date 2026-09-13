using AgentCore.Application.Configuration.Schema;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// One agent's <c>approval:</c> block: the standing rules that answer an approval request before
/// it surfaces. Everything a rule does not name still asks.
/// </summary>
internal static class AgentApproval
{
    /// <summary>Composes one agent's auto-approval patterns.</summary>
    /// <param name="defaults">The <c>agents.defaults</c> section, or <see langword="null"/>.</param>
    /// <param name="agent">The agent to resolve.</param>
    /// <returns>
    /// The agent's own <c>auto:</c> when it declares one, else the defaults', else empty. One key,
    /// so key-by-key inheritance is a single fallback, the same as <c>knowledge:</c> and
    /// <c>compaction:</c>.
    /// </returns>
    public static IReadOnlyList<string> Compose(AgentDefaults? defaults, AgentConfiguration agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return agent.Approval?.Auto ?? defaults?.Approval?.Auto ?? [];
    }

    /// <summary>Whether <paramref name="pattern"/> auto-approves <paramref name="toolName"/>.</summary>
    /// <param name="pattern">One <c>auto:</c> entry: a tool name, or a prefix with a trailing <c>*</c>.</param>
    /// <param name="toolName">The called tool's name.</param>
    /// <returns>
    /// An ordinal prefix match for a trailing-<c>*</c> pattern, else an ordinal exact match. A
    /// <c>*</c> anywhere else is literal, so it matches nothing a document can call.
    /// </returns>
    public static bool Matches(string pattern, string toolName)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(toolName);

        return pattern.EndsWith('*')
            ? toolName.StartsWith(pattern[..^1], StringComparison.Ordinal)
            : toolName == pattern;
    }

    /// <summary>
    /// Bakes one agent's <c>auto:</c> into its pipeline as <c>UseToolApproval</c> rules. No
    /// patterns, no layer — the document pays nothing for approval it never declares.
    /// </summary>
    /// <param name="agent">The compiled agent.</param>
    /// <param name="defaults">The <c>agents.defaults</c> section, or <see langword="null"/>.</param>
    /// <param name="item">The agent being compiled.</param>
    /// <returns>The approval-wrapped agent, or <paramref name="agent"/> unchanged.</returns>
    public static AIAgent Apply(AIAgent agent, AgentDefaults? defaults, AgentConfiguration item)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(item);

        if (Compose(defaults, item) is not { Count: > 0 } auto)
        {
            return agent;
        }

        return new AIAgentBuilder(agent)
            .UseToolApproval(new ToolApprovalAgentOptions { AutoApprovalRules = auto.Select(ToRule).ToList() })
            .Build();
    }

    private static Func<ToolAutoApprovalRuleContext, ValueTask<bool>> ToRule(string pattern)
        => context => ValueTask.FromResult(Matches(pattern, context.FunctionCallContent.Name));
}

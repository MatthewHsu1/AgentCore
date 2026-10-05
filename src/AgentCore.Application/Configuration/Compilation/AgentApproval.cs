using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Layers;
using AgentCore.Application.Runtime.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Configuration.Compilation
{
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
        /// <c>*</c> anywhere else is literal, so it matches nothing a document can conversation.
        /// </returns>
        public static bool Matches(string pattern, string toolName)
        {
            ArgumentNullException.ThrowIfNull(pattern);
            ArgumentNullException.ThrowIfNull(toolName);

            return pattern.EndsWith('*')
                ? toolName.StartsWith(pattern[..^1], StringComparison.Ordinal)
                : toolName == pattern;
        }

        /// <summary>Reads the state keys an agent's approval surface needs persisted across resume.</summary>
        /// <param name="defaults">The <c>agents.defaults</c> section, or <see langword="null"/>.</param>
        /// <param name="item">The agent being compiled.</param>
        /// <param name="tools">The tools the agent offers the model, if any.</param>
        /// <param name="hooks">The hooks of the compile.</param>
        /// <returns>
        /// The pending queue, the standing-rule and the held-answer keys when the agent may surface an approval —
        /// through <c>auto:</c>, which adds the approval layer, through an approval-required tool, or through a
        /// BeforeRun hook, which can add one at run time — else empty. An absent key is never kept, so all three ride together.
        /// </returns>
        public static IReadOnlyList<string> StateKeysFor(
            AgentDefaults? defaults, AgentConfiguration item, IEnumerable<AITool>? tools, HookRuntime hooks)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(hooks);

            return !MayAsk(defaults, item, tools, hooks)
                ? []
                : [PendingApprovalQueue.PendingStateKey, PendingApprovalQueue.StandingStateKey, PendingApprovalQueue.HeldStateKey];
        }

        /// <summary>
        /// Bakes one agent's <c>auto:</c> and the BeforeToolApproval hooks into its pipeline as one <c>UseToolApproval</c> rule,
        /// with the deny layer outside it when a hook overrides BeforeToolApproval, <see cref="ApprovalQueueDrain"/> directly outside
        /// it, and <see cref="ApprovalAnswerHold"/> innermost whenever the agent can ask. An agent that can ask nothing gets no layer — the document pays nothing for
        /// approval it never declares.
        /// </summary>
        /// <param name="agent">The compiled agent.</param>
        /// <param name="defaults">The <c>agents.defaults</c> section, or <see langword="null"/>.</param>
        /// <param name="item">The agent being compiled.</param>
        /// <param name="tools">The tools the agent offers the model, if any.</param>
        /// <param name="hooks">The hooks of the compile.</param>
        /// <returns>The approval-wrapped agent, or <paramref name="agent"/> unchanged.</returns>
        public static AIAgent Apply(
            AIAgent agent, AgentDefaults? defaults, AgentConfiguration item, IEnumerable<AITool>? tools, HookRuntime hooks)
        {
            ArgumentNullException.ThrowIfNull(agent);
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(hooks);

            IReadOnlyList<string> auto = Compose(defaults, item);
            bool gated = hooks.Gates.Overrides(GatePoint.BeforeToolApproval);
            if (!gated && !MayAsk(defaults, item, tools, hooks))
            {
                return agent;
            }

            AIAgent held = ApprovalAnswerHold.Use(new AIAgentBuilder(agent)).Build();
            if (auto.Count == 0 && !gated)
            {
                return held;
            }

            AIAgentBuilder builder = new(held);
            if (gated)
            {
                builder = ApprovalGateLayer.UseDenials(builder);
            }

            return ApprovalQueueDrain.Use(builder)
                .UseToolApproval(new ToolApprovalAgentOptions { AutoApprovalRules = [ApprovalGateLayer.Rule(auto, hooks)] })
                .Build();
        }

        private static bool MayAsk(AgentDefaults? defaults, AgentConfiguration item, IEnumerable<AITool>? tools, HookRuntime hooks)
        {
            return Compose(defaults, item).Count > 0
                || tools?.OfType<ApprovalRequiredAIFunction>().Any() == true
                || hooks.Gates.Overrides(GatePoint.BeforeRun);
        }
    }
}

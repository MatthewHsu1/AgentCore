using AgentCore.Application.Hooks.Notices;

namespace AgentCore.Application.Hooks.BuiltIn
{
    /// <summary>
    /// Which facts the audit chain writes a row for, where a notice type alone does not say. The audit hook writes
    /// by these rules and the telemetry hook counts by them, so the counter never drifts from the rows.
    /// </summary>
    internal static class AuditRules
    {
        /// <summary>A fault the model cannot answer, or a tool the model named that no one declared, is a <c>tool.failed</c> row.</summary>
        internal static bool IsAudited(ToolCalled notice)
        {
            return notice is { Outcome: ToolOutcome.Failed, Fatal: true } or { Outcome: ToolOutcome.Undeclared };
        }

        /// <summary>A refusal after <c>conversation.ended</c> is no row: nothing is appended behind the end.</summary>
        internal static bool IsAudited(TurnRefused notice)
        {
            return !notice.AfterEnd;
        }

        /// <summary>Only flagged words are a <c>prompt.flagged</c> row.</summary>
        internal static bool IsAudited(InputModerated notice)
        {
            return notice.Verdict == InputVerdict.Flagged;
        }
    }
}

using AgentCore.Application.Diagnostics;
using AgentCore.Application.Hooks.Notices;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Hooks.BuiltIn
{
    /// <summary>
    /// Which failure a turn met, the counter it bumps and the line it logs. The telemetry hook and
    /// the logging hook read it from <see cref="TurnCompleted"/>; a turn the store refused raises no such notice, so
    /// the session calls <see cref="Account"/> for it. One mapping, so a counter and a line never disagree.
    /// </summary>
    internal static class TurnFailureAccounting
    {
        /// <summary>The failure row a turn met.</summary>
        internal enum Row
        {
            /// <summary>The turn met none.</summary>
            None,

            /// <summary>The run returned no text.</summary>
            EmptyReply,

            /// <summary>A tool spent its retry budget.</summary>
            ToolBudget,

            /// <summary>The run faulted above the fallback layer.</summary>
            RunFault,
        }

        /// <summary>Reads the row a turn met.</summary>
        /// <param name="outcome">How the turn ended.</param>
        /// <param name="failedInTool">Whether the fallback came from a tool that spent its retry budget.</param>
        /// <returns>The row, or <see cref="Row.None"/>.</returns>
        internal static Row Of(TurnOutcome outcome, bool failedInTool)
        {
            return (outcome, failedInTool) switch
            {
                (TurnOutcome.Empty, _) => Row.EmptyReply,
                (TurnOutcome.Fallback, true) => Row.ToolBudget,
                (TurnOutcome.Fallback or TurnOutcome.Faulted, _) => Row.RunFault,
                _ => Row.None,
            };
        }

        /// <summary>Counts the row on <c>agentcore.turn.failures</c>.</summary>
        /// <param name="row">The row.</param>
        internal static void Count(Row row)
        {
            string? kind = row switch
            {
                Row.EmptyReply => AgentCoreTelemetry.FailureEmptyReply,
                Row.ToolBudget => AgentCoreTelemetry.FailureTool,
                Row.RunFault => AgentCoreTelemetry.FailureRun,
                Row.None => null,
                _ => throw new ArgumentOutOfRangeException(nameof(row), row, "The row is outside the closed set."),
            };

            if (kind is not null)
            {
                AgentCoreTelemetry.RecordFailure(kind);
            }
        }

        /// <summary>Writes the row's line. A tool or run row with no exception writes nothing: the line is its stack trace.</summary>
        /// <param name="logger">Where the line goes.</param>
        /// <param name="conversationId">The conversation.</param>
        /// <param name="turnIndex">The turn.</param>
        /// <param name="row">The row.</param>
        /// <param name="cause">The exception behind the failure, or <see langword="null"/>.</param>
        internal static void Write(ILogger logger, string conversationId, int turnIndex, Row row, Exception? cause)
        {
            switch (row)
            {
                case Row.EmptyReply:
                    Log.EmptyReply(logger, conversationId, turnIndex);
                    break;
                case Row.ToolBudget when cause is not null:
                    Log.ToolBudgetSpent(logger, conversationId, turnIndex, cause);
                    break;
                case Row.RunFault when cause is not null:
                    Log.TurnRunFaulted(logger, conversationId, turnIndex, cause);
                    break;
                case Row.None:
                case Row.ToolBudget:
                case Row.RunFault:
                default:
                    break;
            }
        }

        /// <summary>Counts and logs the row of a turn the store refused, which no hook hears about.</summary>
        /// <param name="logger">Where the line goes.</param>
        /// <param name="conversationId">The conversation.</param>
        /// <param name="turnIndex">The turn.</param>
        /// <param name="outcome">How the turn ended.</param>
        /// <param name="failedInTool">Whether the fallback came from a tool that spent its retry budget.</param>
        /// <param name="cause">The exception behind the failure, or <see langword="null"/>.</param>
        internal static void Account(ILogger logger, string conversationId, int turnIndex, TurnOutcome outcome, bool failedInTool, Exception? cause)
        {
            Row row = Of(outcome, failedInTool);
            Count(row);
            Write(logger, conversationId, turnIndex, row, cause);
        }
    }
}

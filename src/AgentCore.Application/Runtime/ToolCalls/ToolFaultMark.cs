using AgentCore.Application.Runtime.Agents;

namespace AgentCore.Application.Runtime.ToolCalls
{
    /// <summary>
    /// Marks a fault a tool threw, so the turn can tell a tool that spent its retry budget from a run that faulted
    /// outside every tool. <see cref="FallbackAgent"/> catches both, and a model endpoint that did not answer throws
    /// the same exception types a tool's endpoint does.
    /// </summary>
    internal static class ToolFaultMark
    {
        private const string Key = "AgentCore.ToolCallId";

        /// <summary>Marks a fault one tool call let out.</summary>
        /// <param name="fault">What the tool threw.</param>
        /// <param name="toolCallId">The id the model gave the call.</param>
        internal static void Put(Exception fault, string toolCallId)
        {
            ArgumentNullException.ThrowIfNull(fault);

            fault.Data[Key] = toolCallId;
        }

        /// <summary>Reads whether a tool threw this fault, or a fault it wraps.</summary>
        /// <param name="fault">The fault that ended the run.</param>
        /// <returns><see langword="true"/> when a tool call let it out.</returns>
        internal static bool IsOn(Exception fault)
        {
            ArgumentNullException.ThrowIfNull(fault);

            return fault switch
            {
                _ when fault.Data.Contains(Key) => true,
                AggregateException aggregate => aggregate.InnerExceptions.Any(IsOn),
                { InnerException: { } inner } => IsOn(inner),
                _ => false,
            };
        }
    }
}

using AgentCore.Application.Hooks.Notices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Hooks.Layers
{
    /// <summary>
    /// Marks a tool call's outcome beside its arguments, where the wrappers that know it (cache, time limit,
    /// pinned-skill redirect) and the tool middleware that reports it both reach it.
    /// </summary>
    internal static class ToolCallOutcomes
    {
        internal static readonly object CachedKey = new();

        internal static readonly object TimedOutKey = new();

        internal static readonly object RedirectedKey = new();

        internal static void Mark(AIFunctionArguments arguments, object key)
        {
            arguments.Context ??= new Dictionary<object, object?>();
            arguments.Context[key] = true;
        }

        internal static bool Marked(AIFunctionArguments arguments, object key)
        {
            return arguments.Context?.ContainsKey(key) == true;
        }

        internal static ToolOutcome Of(AIFunctionArguments arguments)
        {
            return Marked(arguments, TimedOutKey) ? ToolOutcome.TimedOut
            : Marked(arguments, CachedKey) ? ToolOutcome.Cached
            : ToolOutcome.Ok;
        }
    }
}

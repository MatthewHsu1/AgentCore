using System.Text;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Compaction;

#pragma warning disable MAAI001 // CompactionMessageGroup is the framework's own experimental surface.

/// <summary>
/// Writes a folded tool-call group as the tool's name and the head of its result.
/// </summary>
internal static class CappedToolCallFormatter
{
    private const char Cut = '…';

    /// <summary>Builds a formatter that carries at most <paramref name="maxResultChars"/> of each result.</summary>
    /// <param name="maxResultChars">Characters of each tool result to keep. Must be positive.</param>
    /// <returns>The formatter, for <c>ToolResultCompactionStrategy.ToolCallFormatter</c>.</returns>
    public static Func<CompactionMessageGroup, string> Create(int maxResultChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResultChars);

        return group =>
        {
            ArgumentNullException.ThrowIfNull(group);

            var calls = group.Messages
                .SelectMany(message => message.Contents.OfType<FunctionCallContent>())
                .ToList();

            if (calls.Count == 0)
            {
                return string.Empty;
            }

            // A repeated CallId is a duplicated tool result — a replayed transcript, or a provider
            // that reused an id across parallel calls. MAF's own DefaultToolCallFormatter resolves
            // that the same way: last one written wins.
            var results = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var result in group.Messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>()))
            {
                results[result.CallId] = result.Result?.ToString() ?? string.Empty;
            }

            StringBuilder written = new("[Tool Calls]");
            foreach (var call in calls)
            {
                written.Append('\n').Append(call.Name).Append(':');

                if (!results.TryGetValue(call.CallId, out var result) || result.Length == 0)
                {
                    continue;
                }

                written.Append("\n  - ").Append(result.AsSpan(0, Math.Min(result.Length, maxResultChars)));

                if (result.Length > maxResultChars)
                {
                    written.Append(Cut);
                }
            }

            return written.ToString();
        };
    }
}

#pragma warning restore MAAI001

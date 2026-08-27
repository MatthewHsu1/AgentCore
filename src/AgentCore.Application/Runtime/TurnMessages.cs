using System.Text;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The message shapes one turn builds and filters. Every member is pure: messages in, messages out.
/// </summary>
internal static class TurnMessages
{
    /// <summary>
    /// What opens the <c>system</c> message a graph row's history rides on.
    /// </summary>
    internal const string HistoryPreamble = "Conversation so far:\n";

    /// <summary>
    /// What names the caller in that message.
    /// </summary>
    internal const string CallerLinePrefix = "Caller: ";

    /// <summary>
    /// What names this agent in that message.
    /// </summary>
    internal const string AgentLinePrefix = "You: ";

    /// <summary>
    /// Renders the call so far into the one role a workflow node still recognises.
    /// </summary>
    /// <param name="history">The caller-facing history of this call, oldest first.</param>
    /// <returns>One <c>system</c> message, or <see langword="null"/> on the first turn of a call.</returns>
    internal static ChatMessage? GraphHistory(IReadOnlyList<ChatMessage> history)
    {
        StringBuilder rendered = new();

        foreach (var message in history)
        {
            if (message.Text is not { Length: > 0 } text)
            {
                continue;
            }

            rendered
                .Append(message.Role == ChatRole.User ? CallerLinePrefix : AgentLinePrefix)
                .Append(text)
                .Append('\n');
        }

        return rendered.Length == 0
            ? null
            : new ChatMessage(ChatRole.System, HistoryPreamble + rendered.ToString().TrimEnd('\n'));
    }

    /// <summary>Reads whether one update carries something a host needs.</summary>
    /// <param name="update">One update of the run.</param>
    /// <returns>Whether the host reads it.</returns>
    internal static bool CarriesContent(ChatResponseUpdate update)
        => update.Contents.Any(content => content is not TextContent text || text.Text.Length > 0);

    /// <summary>Keeps the tool calls whose results arrived, and drops every unpaired call and every word.</summary>
    /// <param name="messages">Every message the round produced.</param>
    /// <returns>The tool content that carries a complete call-and-result pair, in its original order.</returns>
    internal static List<ChatMessage> FinishedToolMessages(IList<ChatMessage> messages)
    {
        HashSet<string> answered = [];
        foreach (var message in messages)
        {
            foreach (var result in message.Contents.OfType<FunctionResultContent>())
            {
                answered.Add(result.CallId);
            }
        }

        List<ChatMessage> kept = [];
        foreach (var message in messages)
        {
            // A parallel round can finish one call and leave a sibling call in the same message
            // mid-flight. The rule is per call id and not per message, so only the unfinished call
            // is stripped out; the finished one, whose side effect already ran, stays in place.
            List<AIContent> tools =
            [
                .. message.Contents.Where(content => content switch
                {
                    TextContent => false,
                    FunctionCallContent call => answered.Contains(call.CallId),
                    _ => true,
                }),
            ];

            if (!tools.Any(content => content is FunctionCallContent or FunctionResultContent))
            {
                // Plain prose, or a message whose every call is still in flight. Neither belongs in
                // the next turn.
                continue;
            }

            if (tools.Count == message.Contents.Count)
            {
                // Nothing was stripped, so the message is already the finished shape. Keep it whole,
                // contents and order unchanged.
                kept.Add(message);
                continue;
            }

            var trimmed = message.Clone();
            trimmed.Contents = tools;
            kept.Add(trimmed);
        }

        return kept;
    }
}

namespace AgentCore.AspNetCore.Voice.Speech.Replies
{
    /// <summary>One event of an engine turn, in stream order.</summary>
    /// <param name="Kind">What happened.</param>
    /// <param name="Value">The text for <see cref="ReplyEventKind.Text"/>, the call id for a tool event, else empty.</param>
    /// <param name="ToolName">The tool a <see cref="ReplyEventKind.ToolCall"/> calls, else <see langword="null"/>.</param>
    internal readonly record struct ReplyEvent(ReplyEventKind Kind, string Value, string? ToolName = null);
}

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>What one <see cref="ReplyEvent"/> read off the engine stream carries (plan 2.1).</summary>
    internal enum ReplyEventKind
    {
        /// <summary>Non-empty text of the current step.</summary>
        Text,

        /// <summary>A <c>FunctionCallContent</c>: the step's words are over and its tools start.</summary>
        ToolCall,

        /// <summary>A <c>FunctionResultContent</c>.</summary>
        ToolResult,

        /// <summary>The result that answers the last open call of the round: the next step starts.</summary>
        RoundDone,
    }
}

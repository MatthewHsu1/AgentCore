namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>The four signed-off voice latency readings.</summary>
    public enum LatencyMetric
    {
        /// <summary>From the start of one model step to its first text or tool call. One reading per step.</summary>
        TimeToFirstToken,

        /// <summary>From the user's final words to the first text of the reply handed to the output.</summary>
        TimeToFirstSpeech,

        /// <summary>From the user's final words to the end of the last step of a reply nothing cut short. A cut reply gets no reading.</summary>
        TimeToReplyEnd,

        /// <summary>From the transport reporting a barge-in to every speech it interrupted being done.</summary>
        BargeIn,
    }
}

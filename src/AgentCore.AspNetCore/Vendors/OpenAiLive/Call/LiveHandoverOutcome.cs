namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>Whether a line took a GPT-Live call.</summary>
    internal enum LiveHandoverOutcome
    {
        /// <summary>The line did not take it; the caller is still on the AI leg.</summary>
        NotTaken = 0,

        /// <summary>The line took it, and the AI leg is still up for this side to hang up.</summary>
        Taken = 1,

        /// <summary>The line took it, and the peer already closed the AI leg.</summary>
        TakenAndClosed = 2,
    }
}

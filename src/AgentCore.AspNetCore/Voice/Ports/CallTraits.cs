namespace AgentCore.AspNetCore.Voice.Ports
{
    /// <summary>What a conversation vendor does itself, so AgentCore does not.</summary>
    [Flags]
    public enum CallTraits
    {
        /// <summary>AgentCore's own speech layer runs the conversation.</summary>
        None = 0,

        /// <summary>The vendor makes the caller's audio itself and takes text from AgentCore, so no <c>providers.speech</c> is needed.</summary>
        SpeaksForItself = 1,

        /// <summary>The vendor decides when the caller's turn ends, so AgentCore's away prompt and tool fillers never run.</summary>
        TakesTurnsItself = 2,
    }
}

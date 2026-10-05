using AgentCore.Application.Hooks.Notices;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>One closed spoken line.</summary>
    /// <param name="Speaker">Who said it.</param>
    /// <param name="Text">The words, trimmed.</param>
    /// <param name="StartMs">The first delta's start, in milliseconds from the session's start.</param>
    /// <param name="EndMs">The last delta's end, in milliseconds from the session's start.</param>
    internal sealed record LiveLine(Speaker Speaker, string Text, int StartMs, int EndMs);
}

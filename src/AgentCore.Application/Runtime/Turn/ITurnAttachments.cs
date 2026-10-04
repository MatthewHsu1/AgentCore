using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Turn
{
    /// <summary>
    /// One kind of thing a turn's tools produce for the caller, as the invoking client handles every
    /// kind alike: drained onto the result message of the outermost tool call it was filed under.
    /// </summary>
    internal interface ITurnAttachments
    {
        /// <summary>Takes what was filed under one outer tool call, in publish order.</summary>
        /// <param name="callId">The call whose content to take.</param>
        /// <returns>What that call filed, or empty.</returns>
        IReadOnlyList<AIContent> TakeFor(string callId);
    }
}

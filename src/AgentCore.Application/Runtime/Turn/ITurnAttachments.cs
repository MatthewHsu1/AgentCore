using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Turn
{
    /// <summary>
    /// One kind of thing a turn's tools produce for the caller, as the invoking client handles every
    /// kind alike: opened around the outermost tool call, drained onto that call's result message.
    /// </summary>
    /// <remarks>
    /// A new kind implements this once and joins <c>TurnInvocation.Attachments()</c>. The invoking client
    /// never names a kind.
    /// </remarks>
    internal interface ITurnAttachments
    {
        /// <summary>Opens one outermost tool call as the key publishes file under.</summary>
        /// <param name="callId">The id of the outermost tool call now running.</param>
        /// <returns>The scope. Disposing it puts back the key that was open before.</returns>
        IDisposable BeginOuterCall(string callId);

        /// <summary>Takes what was filed under one outer tool call, in publish order.</summary>
        /// <param name="callId">The call whose content to take.</param>
        /// <returns>What that call filed, or empty.</returns>
        IReadOnlyList<AIContent> TakeFor(string callId);
    }
}

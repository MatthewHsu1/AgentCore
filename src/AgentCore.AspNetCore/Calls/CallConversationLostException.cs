namespace AgentCore.AspNetCore.Calls
{
    /// <summary>
    /// The call's conversation was unloaded and another call now holds it (one live call per conversation). The call
    /// can no longer ask anything; its vendor hangs it up.
    /// </summary>
    internal sealed class CallConversationLostException(string callId, string conversationId)
        : InvalidOperationException($"the call {callId} lost conversation {conversationId} to another call.");
}

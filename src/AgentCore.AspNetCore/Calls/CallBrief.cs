using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Calls
{
    /// <summary>
    /// The brief the call gate accepted a call with. It is filed under the conversation id, not the call, so a call must
    /// clear what an earlier call filed and must not clear what a newer call filed.
    /// </summary>
    internal sealed class CallBrief(PhoneCallHost host, string conversationId, string? text)
    {
        internal string? Text => text;

        /// <summary>Files the brief, or clears an earlier call's when this call brought none.</summary>
        internal void File()
        {
            // A vendor that speaks for itself files an entry even with no brief: the engine must still get the front-voice note.
            if (string.IsNullOrEmpty(text) && !host.FrontVoice)
            {
                Clear();
            }
            else
            {
                host.Briefs?.Set(conversationId, string.IsNullOrEmpty(text) ? null : text, host.FrontVoice);
            }
        }

        internal void Clear()
        {
            host.Briefs?.Clear(conversationId);
        }

        // A newer session that another call holds carries that call's brief, so only then is it left in place.
        internal void Forget(ConversationSession? holder, ConversationSession own)
        {
            if (PhoneCall.Owns(holder, own) || !holder!.Lifetime.Ending.HasCall)
            {
                Clear();
            }
        }
    }
}

using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Turn
{
    /// <summary>
    /// A content part meant for the turn's own caller, not for the model or the transcript. A notice
    /// rides one merged update to the streaming consumer and nothing else: never persiste.
    /// </summary>
    public abstract class NoticeContent : AIContent
    {
        /// <summary>Creates a notice.</summary>
        protected NoticeContent()
        {
        }
    }
}

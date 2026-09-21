namespace AgentCore.Application.Runtime.Turn
{
    /// <summary>One outermost tool call, open on every kind of attachment at once.</summary>
    internal sealed class OuterCall : IDisposable
    {
        private readonly IDisposable[] _opened;

        private OuterCall(IDisposable[] opened)
        {
            _opened = opened;
        }

        /// <summary>Opens the call on every kind, in list order.</summary>
        /// <param name="attachments">Every kind the turn carries.</param>
        /// <param name="callId">The id of the outermost tool call now running.</param>
        /// <returns>The scope. Disposing it closes every kind, last opened first.</returns>
        public static OuterCall Open(IReadOnlyList<ITurnAttachments> attachments, string callId)
        {
            return new([.. attachments.Select(kind => kind.BeginOuterCall(callId))]);
        }

        public void Dispose()
        {
            for (int index = _opened.Length - 1; index >= 0; index--)
            {
                _opened[index].Dispose();
            }
        }
    }
}

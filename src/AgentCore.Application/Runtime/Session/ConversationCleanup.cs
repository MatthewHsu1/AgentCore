namespace AgentCore.Application.Runtime.Session
{
    /// <summary>
    /// Disposes a conversation's shells and deletes its workspace once no tool of the conversation still runs in them,
    /// or once the end backstop stopped those tools (<see cref="ConversationEnding.ToolGrace"/>). A tool keeps its
    /// folder through its grace: a cut, a withdraw or the end never takes the disk from under a call that started.
    /// </summary>
    /// <param name="session">The conversation.</param>
    internal sealed class ConversationCleanup(ConversationSession session)
    {
        private readonly Lock _gate = new();

        private Task _pending = Task.CompletedTask;

        /// <summary>Gets what completes once every cleanup asked for so far is done.</summary>
        internal Task Pending
        {
            get
            {
                lock (_gate)
                {
                    return _pending;
                }
            }
        }

        /// <summary>Cleans up now when no tool runs, and otherwise once the running tools ended or were stopped.</summary>
        /// <param name="shells">Whether the shells go too, and not only the workspace.</param>
        /// <returns>Completes once the cleanup is done, or once it is left to run after the tools.</returns>
        internal Task AfterToolsAsync(bool shells)
        {
            if (session.ToolRuns.IsIdle)
            {
                return CleanAsync(shells);
            }

            Task later = LaterAsync(shells);
            lock (_gate)
            {
                _pending = Task.WhenAll(_pending, later);
            }

            return Task.CompletedTask;
        }

        private async Task LaterAsync(bool shells)
        {
            _ = await Task.WhenAny(session.ToolRuns.WhenIdle(), session.Lifetime.Ending.WhenToolsStopped).ConfigureAwait(false);
            await CleanAsync(shells).ConfigureAwait(false);
        }

        private async Task CleanAsync(bool shells)
        {
            try
            {
                if (shells)
                {
                    await session.Lifetime.DisposeShellsAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                session.Lifetime.DeleteWorkspace();
            }
        }
    }
}

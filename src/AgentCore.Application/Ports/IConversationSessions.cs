using AgentCore.Application.Conversation;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Ports
{
    /// <summary>
    /// Owns the session of a conversation for as long as the conversation needs it, for every entry the
    /// document declares. One conversation id has at most one live session, under one entry.
    /// </summary>
    public interface IConversationSessions
    {
        /// <summary>
        /// Returns the live session of one conversation under one entry, or opens it when none is held.
        /// </summary>
        /// <param name="entry">The entry the session is held under, or opens under.</param>
        /// <param name="conversationId">The id the host gives the conversation, or <see langword="null"/> to be given one.</param>
        /// <param name="state">
        /// The stored state to build the session from. It is used only when this call builds the session; a live
        /// session keeps its own state, and this is ignored.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancels this caller's wait for a build or a close another caller started. That build or close runs on.
        /// </param>
        /// <returns>
        /// The session, ready for its next turn. Two callers that ask for the same id under the same entry at once
        /// get the same session, built once. When the id's session is closing, this waits until its teardown is
        /// done (its last words flushed, its shells stopped, its workspace folder deleted), then opens a new one.
        /// </returns>
        /// <exception cref="ConversationInUseException">
        /// <paramref name="conversationId"/> is held, or being opened, under another entry. Nothing was built, and
        /// that entry's session is left running, untouched. Once that session is gone (closed or unloaded), this
        /// entry may open the id and read its history.
        /// </exception>
        ValueTask<ConversationSession> GetOrOpenAsync(
            string entry, string? conversationId, ConversationSessionState? state, CancellationToken cancellationToken = default);

        /// <summary>Reads the session of one conversation under one entry, and marks the conversation as still live.</summary>
        /// <param name="entry">The entry the session must be held under.</param>
        /// <param name="conversationId">The id the request named. It is the <see cref="ConversationSession.ConversationId"/>.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>
        /// The session, or <see langword="null"/> when this holds no usable session of that id under this entry:
        /// none is held, it is held under another entry, or it is still being built or already closing. A lookup
        /// never waits and never closes anything.
        /// </returns>
        ValueTask<ConversationSession?> TryGetAsync(string entry, string conversationId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Closes one conversation's session: waits for the words it still owes the message store, disposes its background
        /// children and shells, then deletes its workspace folder. This writes no <c>conversation.ended</c>
        /// event; a caller that wants the close to be a real end calls
        /// <see cref="ConversationSession.EndConversation"/> itself, before this. The id stays taken until the
        /// teardown is done, so an open of it waits rather than race the folder delete.
        /// </summary>
        /// <param name="entry">The entry the session must be held under.</param>
        /// <param name="conversationId">The id of the conversation to close.</param>
        /// <param name="cancellationToken">Cancels only a wait for a close another caller already started.</param>
        /// <returns>
        /// A task that completes once the session is gone and its writes are done. When this holds no live session
        /// of that id under this entry, nothing is closed.
        /// </returns>
        ValueTask CloseAsync(string entry, string conversationId, CancellationToken cancellationToken = default);
    }
}

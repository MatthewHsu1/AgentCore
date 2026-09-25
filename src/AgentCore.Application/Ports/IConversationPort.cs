using AgentCore.Application.Runtime;
using AgentCore.Domain;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Ports
{
    /// <summary>
    /// The inbound seam of one conversation: start or continue a turn, stream the reply, cancel it.
    /// </summary>
    public interface IConversationPort
    {
        /// <summary>Gets the id of the conversation.</summary>
        string ConversationId { get; }

        /// <summary>Gets the stage the machine holds. It is empty when the document declares no policy.</summary>
        string Stage { get; }

        /// <summary>Gets whether the conversation reached a terminal stage. A document with no policy never does.</summary>
        bool IsComplete { get; }

        /// <summary>Gets or sets the knowledge scope the host opened for this conversation, or null for none.</summary>
        /// <remarks>Set it before the run: every turn composes its own scope from it. It outlives turns by construction — one conversation hears one customer.</remarks>
        KnowledgeScope? Scope { get; set; }

        /// <summary>Gets the turn that finished last, or <see langword="null"/> before the first turn ends.</summary>
        TurnResult? LastTurn { get; }

        /// <summary>Runs one turn end to end, and returns what it did.</summary>
        /// <param name="userInput">What the caller said.</param>
        /// <param name="cancellationToken">Cancels the model calls.</param>
        /// <returns>The finished turn. It always carries a spoken line.</returns>
        /// <exception cref="InvalidOperationException">
        /// The conversation already ended, or another turn of this conversation is still running.
        /// </exception>
        Task<TurnResult> RunTurnAsync(string userInput, CancellationToken cancellationToken = default);

        /// <summary>Runs one turn end to end from a message the caller built, and returns what it did.</summary>
        /// <param name="userInput">What the caller said or answered: words, an approval answer, or both.</param>
        /// <param name="cancellationToken">Cancels the model calls.</param>
        /// <returns>The finished turn. Its reply is empty while approval requests are pending.</returns>
        /// <exception cref="InvalidOperationException">
        /// The conversation already ended, or another turn of this conversation is still running.
        /// </exception>
        Task<TurnResult> RunTurnMessageAsync(ChatMessage userInput, CancellationToken cancellationToken);

        /// <summary>Runs one turn and streams the reply as it arrives.</summary>
        /// <param name="userInput">What the caller said.</param>
        /// <param name="cancellationToken">Cancels the model calls.</param>
        /// <returns>The reply, one update at a time. Every update carries content.</returns>
        /// <remarks>
        /// The turn finishes when the enumeration finishes, and <see cref="LastTurn"/> holds it after
        /// that. The stream carries no lifecycle update: section 8.6 measured seven of those in one
        /// 40-fragment reply, and an adapter must not have to filter them again.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The conversation already ended, or another turn of this conversation is still running.
        /// </exception>
        IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingAsync(
            string userInput,
            CancellationToken cancellationToken = default);

        /// <summary>Runs one turn from a message the caller built and streams the reply as it arrives.</summary>
        /// <param name="userInput">What the caller said or answered: words, an approval answer, or both.</param>
        /// <param name="cancellationToken">Cancels the model calls.</param>
        /// <returns>The reply, one update at a time. Every update carries content.</returns>
        /// <exception cref="InvalidOperationException">
        /// The conversation already ended, or another turn of this conversation is still running.
        /// </exception>
        IAsyncEnumerable<ChatResponseUpdate> RunTurnMessageStreamingAsync(
            ChatMessage userInput,
            CancellationToken cancellationToken);

        /// <summary>Starts one turn, and hands back its index and its reply as it arrives.</summary>
        /// <param name="userInput">What the caller said or answered: words, an approval answer, or both.</param>
        /// <param name="origin">Where the turn hangs in the conversation the caller can see, or <see langword="null"/>.</param>
        /// <param name="cancellationToken">Cancels the turn, from the start through its last update.</param>
        /// <returns>
        /// The started turn. <see cref="Cut"/> reaches it by <see cref="TurnRun.TurnIndex"/> from now on. Enumerate
        /// <see cref="TurnRun.Updates"/> once: the turn runs and commits only then. Dispose the run, read or not:
        /// the conversation is freed when the reply is read to the end or the run is disposed.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// The conversation already ended, or another turn of this conversation is still running.
        /// </exception>
        Task<TurnRun> StartTurnAsync(
            ChatMessage userInput, ConversationTurnOrigin? origin, CancellationToken cancellationToken = default);

        /// <summary>Cuts one turn's reply where the user stopped seeing or hearing it (design section 3, the Cut rule).</summary>
        /// <param name="turnIndex">The turn the user was seeing or hearing.</param>
        /// <param name="cut">
        /// What reached the user. <see cref="TurnCut.ShownText"/> <see langword="null"/> keeps everything the turn yielded.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the cut was recorded: a running turn stops and commits with it, and a committed
        /// turn has its reply rewritten. <see langword="false"/> when the turn is unknown or already cut, or when it is
        /// older than the newest turn started on this conversation: once a later turn starts, an earlier turn's reply
        /// never changes, even while the later turn is still running.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException"><see cref="TurnCut.Played"/> is negative.</exception>
        bool Cut(int turnIndex, TurnCut cut);

        /// <summary>Replaces the cut a turn already took with a later account of what reached the user.</summary>
        /// <param name="turnIndex">The turn the user was seeing or hearing.</param>
        /// <param name="cut">What reached the user, as it is now known.</param>
        /// <returns>
        /// <see langword="true"/> when the recut was recorded: the turn commits with it, or its committed reply is
        /// rewritten to it. <see langword="false"/> when the turn is unknown or took no cut, or when it is older than
        /// the newest turn started on this conversation, under the same rule as <see cref="Cut"/>.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException"><see cref="TurnCut.Played"/> is negative.</exception>
        bool Recut(int turnIndex, TurnCut cut);
    }
}

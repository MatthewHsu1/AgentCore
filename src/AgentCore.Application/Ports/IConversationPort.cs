using AgentCore.Domain;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Ports;

/// <summary>
/// The inbound seam of one call: start or continue a turn, stream the reply, cancel it.
/// </summary>
public interface IConversationPort
{
    /// <summary>Gets the id of the call.</summary>
    string CallId { get; }

    /// <summary>Gets the stage the machine holds. It is empty when the document declares no policy.</summary>
    string Stage { get; }

    /// <summary>Gets whether the call reached a terminal stage. A document with no policy never does.</summary>
    bool IsComplete { get; }

    /// <summary>Gets or sets the knowledge scope the host opened for this call, or null for none.</summary>
    /// <remarks>Set it before the run: every turn composes its own scope from it. It outlives turns by construction — one call hears one customer.</remarks>
    KnowledgeScope? Scope { get; set; }

    /// <summary>Gets the turn that finished last, or <see langword="null"/> before the first turn ends.</summary>
    TurnResult? LastTurn { get; }

    /// <summary>Runs one turn end to end, and returns what it did.</summary>
    /// <param name="userInput">What the caller said.</param>
    /// <param name="cancellationToken">Cancels the model calls.</param>
    /// <returns>The finished turn. It always carries a spoken line.</returns>
    /// <exception cref="InvalidOperationException">
    /// The call already ended, or another turn of this call is still running.
    /// </exception>
    Task<TurnResult> RunTurnAsync(string userInput, CancellationToken cancellationToken = default);

    /// <summary>Runs one turn end to end from a message the caller built, and returns what it did.</summary>
    /// <param name="userInput">What the caller said or answered: words, an approval answer, or both.</param>
    /// <param name="cancellationToken">Cancels the model calls.</param>
    /// <returns>The finished turn. Its reply is empty while approval requests are pending.</returns>
    /// <exception cref="InvalidOperationException">
    /// The call already ended, or another turn of this call is still running.
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
    /// The call already ended, or another turn of this call is still running.
    /// </exception>
    IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingAsync(
        string userInput,
        CancellationToken cancellationToken = default);

    /// <summary>Runs one turn from a message the caller built and streams the reply as it arrives.</summary>
    /// <param name="userInput">What the caller said or answered: words, an approval answer, or both.</param>
    /// <param name="cancellationToken">Cancels the model calls.</param>
    /// <returns>The reply, one update at a time. Every update carries content.</returns>
    /// <exception cref="InvalidOperationException">
    /// The call already ended, or another turn of this call is still running.
    /// </exception>
    IAsyncEnumerable<ChatResponseUpdate> RunTurnMessageStreamingAsync(
        ChatMessage userInput,
        CancellationToken cancellationToken);

    /// <summary>Ends the running turn where the caller cut the reply off.</summary>
    /// <param name="utteranceUntilInterrupt">The text the caller actually heard.</param>
    /// <param name="durationUntilInterrupt">How much of the reply played, as the relay reported it.</param>
    /// <param name="cutsRunningTurn">
    /// Whether the turn running now is the turn the caller was hearing. An adapter that paces no
    /// audio of its own leaves this at <see langword="true"/> and lets the implementation decide.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the barge-in was recorded, either against the running turn or
    /// against the turn that finished last, and <see langword="false"/> when there was nothing to
    /// record it against.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Item 6a of section 11 asks the record to hold what the caller heard, not what the model
    /// produced. Section 7.1 reports both values on the <c>interrupt</c> frame, at 1 ms, so an
    /// adapter passes them through and never computes them. Item 6c forbids the estimator that would
    /// otherwise stand here.
    /// </para>
    /// <para>
    /// <paramref name="cutsRunningTurn"/> carries one domain fact and no frame schema, so D8 holds.
    /// A vendor that paces the audio itself is still speaking one turn while the next one already
    /// runs — a held prompt starts it inside the finished turn's own ending — and only that adapter
    /// can tell the two apart. It answers <see langword="false"/> there, and the turn the caller was
    /// actually hearing is the one the record corrects.
    /// </para>
    /// </remarks>
    bool Interrupt(string utteranceUntilInterrupt, TimeSpan durationUntilInterrupt, bool cutsRunningTurn = true);
}

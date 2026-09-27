using AgentCore.Application.Conversation;
using AgentCore.Application.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Transcript
{
    /// <summary>One turn as it is sealed: the one durable append of that turn.</summary>
    /// <param name="User">The message the user sent. It is always written first.</param>
    /// <remarks>Design: docs/handoff/2026-09-22-maf-native-engine-design.md, section 2 "Types" and section 3.</remarks>
    internal sealed record TurnCommit(ChatMessage User)
    {
        /// <summary>Gets what the run yielded as the caller saw it, or <see langword="null"/> when it yielded nothing.</summary>
        public AgentResponse? Seen { get; init; }

        /// <summary>Gets what the turn layers reported about the run, or <see langword="null"/> when no layer marked it.</summary>
        public TurnDisposition? Disposition { get; init; }

        /// <summary>Gets the cut that stopped the turn, or <see langword="null"/> when nothing cut it.</summary>
        public TurnCut? Cut { get; init; }

        /// <summary>
        /// Gets whether the run answered in full: nothing cut it and no fallback replaced its reply. A turn that did
        /// not keeps only its finished tool pairs and <see cref="Reply"/>.
        /// </summary>
        public bool Completed { get; init; } = true;

        /// <summary>
        /// Gets whether the conversation keeps only what the user said and was shown, as a graph row does: its
        /// nodes' tool pairs and lines to each other are neither.
        /// </summary>
        public bool CallerFacing { get; init; }

        /// <summary>
        /// Gets the line the user was shown, when the turn is caller-facing or did not complete, or
        /// <see langword="null"/> to read it from <see cref="Cut"/> and <see cref="Seen"/>.
        /// </summary>
        public string? Reply { get; init; }

        /// <summary>Gets the state that rides the same batch as the words, or <see langword="null"/> for none.</summary>
        public ConversationSessionState? State { get; init; }

        /// <summary>Gets what the caller calls <see cref="User"/>, or <see langword="null"/> to name it in the append.</summary>
        public string? UserMessageId { get; init; }
    }
}

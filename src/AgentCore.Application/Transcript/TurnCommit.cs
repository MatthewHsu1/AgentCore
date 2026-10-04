using AgentCore.Application.Conversation;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Transcript
{
    /// <summary>One turn as it is sealed: the one durable append of that turn.</summary>
    /// <param name="User">The message the user sent. It is written first, after <see cref="Before"/>.</param>
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

        /// <summary>Gets the files a caller-facing turn handed the caller as cards. The reply keeps them, as a single agent's turn keeps its cards.</summary>
        public IReadOnlyList<FileContent> Files { get; init; } = [];

        /// <summary>
        /// Gets the line the user was shown, when the turn is caller-facing or did not complete, or
        /// <see langword="null"/> to read it from <see cref="Cut"/> and <see cref="Seen"/>.
        /// </summary>
        public string? Reply { get; init; }

        /// <summary>Gets the state that rides the same batch as the words, or <see langword="null"/> for none.</summary>
        public ConversationSessionState? State { get; init; }

        /// <summary>
        /// Gets the call ids whose approval requests the record leaves out: a hook denied them, so no person was asked
        /// and the record keeps the call and its refusal; or an earlier turn already stored them and this turn only
        /// showed them again. A stored request with no stored answer, or one stored twice, would break every later run
        /// of the conversation.
        /// </summary>
        public IReadOnlyCollection<string> Unasked { get; init; } = [];

        /// <summary>
        /// Gets the tool calls and results of the turns this turn's edit withdrew. They are written right after
        /// <see cref="User"/>, whatever else the turn keeps, because those tools already ran.
        /// </summary>
        public IReadOnlyList<ChatMessage> Carried { get; init; } = [];

        /// <summary>
        /// Gets what was said on a call ahead of <see cref="User"/> that no turn answered, oldest first: the caller's
        /// words and the <see cref="FrontVoice"/> lines that answered them. They are written just before it.
        /// </summary>
        public IReadOnlyList<ChatMessage> Before { get; init; } = [];

        /// <summary>Gets what the caller calls <see cref="User"/>, or <see langword="null"/> to name it in the append.</summary>
        public string? UserMessageId { get; init; }
    }
}

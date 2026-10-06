using System.Text.Json;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Commands;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Policy;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.State;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AgentCore.Application.Runtime.Agents;
using AgentCore.Application.Runtime.Clarification;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.ToolCalls;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Runtime.Session
{
    /// <summary>
    /// The turn loop of one conversation. It owns the state, the stage machine, and the transcript.
    /// </summary>
    public sealed class ConversationSession : IConversationPort, IAsyncDisposable
    {
        /// <summary>
        /// The line the caller hears when a turn fails and the document names none.
        /// </summary>
        public const string FallbackReply = AgentCoreConfiguration.DefaultFallbackReply;

        internal StagePolicy? Policy { get; }

        internal StateExtractor? Extractor { get; }

        internal CounterStateWriter Counters { get; }

        internal TimeProvider Time { get; }

        internal DateTimeOffset StartedAt { get; }

        internal AgentCoreChatHistoryProvider History { get; }

        internal bool SessionCarriesHistory { get; }

        internal ILogger Logger { get; }

        internal Lock TurnLock { get; } = new();

        internal ConversationShells? Shells { get; }

        internal ConversationWorkspace? WorkspaceRoot { get; }

        internal AgentSession? AgentSession { get; set; }

        /// <summary>
        /// The conversation's shared workflow session on a graph row whose document declares a harness
        /// switch. MAF restores the participants' provider state from its checkpoints on every
        /// turn; any other conversation runs every turn fresh and leaves this <see langword="null"/>.
        /// </summary>
        internal AgentSession? GraphSession { get; set; }

        /// <summary>
        /// The last serialization of <see cref="GraphSession"/>, read back at resume. Refreshed
        /// at every turn end; a host snapshotting mid-turn gets the last turn boundary.
        /// </summary>
        internal JsonElement? GraphBlob { get; set; }

        internal Guid? AmendableEventId { get; set; }

        /// <summary>The words the rows of the turn <see cref="AmendableEventId"/> names say now.</summary>
        internal string? AmendableText { get; set; }

        internal ConversationSessionState? Checkpoint { get; set; }

        internal Clarifications Clarifications { get; } = new();

        internal ConversationTurnRunner Runner { get; }

        internal ConversationTurnStream Stream { get; }

        internal ConversationTurnWriters Writers { get; }

        internal ConversationCutTracker Cuts { get; }

        internal ConversationTranscriptLedger Ledger { get; }

        internal ConversationSessionStateStore States { get; }

        internal ConversationSessionLifetime Lifetime { get; }

        internal ConversationBusyMark Busy { get; }

        internal ConversationToolRuns ToolRuns { get; }

        internal FinishedToolPairs ToolPairs { get; }

        internal ChannelCommands Commands { get; }

        /// <summary>
        /// Creates the session of one conversation.
        /// </summary>
        internal ConversationSession(
            string conversationId,
            ConversationSessionSeams seams,
            ConversationWorkspace? workspace = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(conversationId);
            ArgumentNullException.ThrowIfNull(seams);

            (CompiledAgent? compiled, IGuardEvaluator? guards, StateExtractor? extractor, TimeProvider? timeProvider, ILogger? logger, HookRuntime? hooks) = seams;

            ConversationId = conversationId;
            Logger = logger ?? NullLogger.Instance;
            WorkspaceRoot = workspace;
            Shells = workspace is null ? null : new ConversationShells(workspace.Path, Logger);
            Compiled = compiled;
            History = compiled.History;
            SessionCarriesHistory = compiled.SessionCarriesHistory;
            Extractor = extractor;
            Counters = new CounterStateWriter(guards);
            Time = timeProvider;
            Hooks = new SessionHooks(hooks, conversationId, compiled.EntryName, timeProvider);
            StartedAt = timeProvider.GetUtcNow();
            Policy = compiled.Policy is null ? null : compiled.CreatePolicy(guards);

            State = new StateDocument(compiled.Configuration, Policy?.Stage);

            _ = ConstStateWriter.Apply(State);

            Runner = new ConversationTurnRunner(this);
            Stream = new ConversationTurnStream(this);
            Writers = new ConversationTurnWriters(this);
            Cuts = new ConversationCutTracker(this);
            Ledger = new ConversationTranscriptLedger(this);
            States = new ConversationSessionStateStore(this);
            Lifetime = new ConversationSessionLifetime(this);
            Busy = new ConversationBusyMark(this);
            ToolPairs = new FinishedToolPairs(this);
            ToolRuns = new ConversationToolRuns(ToolPairs.Keep);
            Commands = new ChannelCommands(this);
        }

        /// <summary>
        /// Gets the id of the conversation.
        /// </summary>
        public string ConversationId { get; }

        internal SessionHooks Hooks { get; }

        /// <summary>
        /// Gets the folder this conversation owns on disk, or <see langword="null"/> when the host bound no
        /// workspace root. The folder is deleted once the conversation ended and no tool still runs in it, at most
        /// 30 s after the end.
        /// </summary>
        public string? Workspace => WorkspaceRoot?.Path;

        /// <summary>
        /// Gets the stage the machine holds. It is empty when the entry declares no policy.
        /// </summary>
        public string Stage => State.Stage;

        /// <summary>
        /// Gets whether the conversation reached a terminal stage. An entry with no policy never does.
        /// </summary>
        public bool IsComplete { get; internal set; }

        /// <summary>
        /// Gets the state of this conversation. Every guard and every increment rule reads it.
        /// </summary>
        public StateDocument State { get; }

        /// <summary>
        /// Gets or sets the knowledge scope the host opened for this conversation, or null for none. Set it
        /// before the run; every turn composes its own scope from it.
        /// </summary>
        public KnowledgeScope? Scope { get; set; }

        /// <summary>
        /// Gets the conversation, oldest first. Every stage of the conversation reads it.
        /// </summary>
        public IReadOnlyList<ChatMessage> Transcript
            => Ledger.Session() is { } session ? History.Read(session) : [];

        /// <summary>
        /// Gets the turn that finished last, or <see langword="null"/> before the first turn ends.
        /// </summary>
        public TurnResult? LastTurn { get; internal set; }

        /// <summary>
        /// Gets the name the last written message was stored under, for a caller to hang an edit off.
        /// </summary>
        public string? LastReplyMessageId { get; internal set; }

        /// <summary>
        /// Gets the compiled agent this session runs. Every conversation shares it.
        /// </summary>
        public CompiledAgent Compiled { get; }

        /// <summary>
        /// Whether this conversation keeps one workflow session across turns: a graph row whose document
        /// declares a harness switch. Everything else either carries history on a long-lived
        /// session already (rows 1 and 2) or keeps no provider state at all.
        /// </summary>
        internal bool ReusesGraphSession => !SessionCarriesHistory && Compiled.HarnessStateKeys.Count > 0;

        /// <summary>
        /// Gives the runs this conversation delegates through one tool a set of tools of their own.
        /// </summary>
        public void SetDelegatedTools(string delegatingToolId, IReadOnlyList<AITool> tools)
        {
            Runner.SetDelegatedTools(delegatingToolId, tools);
        }

        /// <summary>
        /// Runs one turn end to end, and returns what it did.
        /// </summary>
        public Task<TurnResult> RunTurnAsync(string userInput, CancellationToken cancellationToken = default)
        {
            return RunTurnAtOriginAsync(userInput, origin: null, cancellationToken);
        }

        /// <summary>
        /// Runs one turn end to end from a message the caller built, and returns what it did.
        /// </summary>
        public Task<TurnResult> RunTurnMessageAsync(ChatMessage userInput, CancellationToken cancellationToken)
        {
            return RunTurnMessageAtOriginAsync(userInput, origin: null, cancellationToken);
        }

        /// <summary>
        /// Runs one turn that knows where it sits in the conversation the caller can see.
        /// </summary>
        public Task<TurnResult> RunTurnAtOriginAsync(
            string userInput, ConversationTurnOrigin? origin, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(userInput);

            return Stream.RunTurnCoreAsync(new ChatMessage(ChatRole.User, userInput), origin, cancellationToken);
        }

        /// <summary>
        /// Runs one turn from a message the caller built, that knows where it sits in the conversation
        /// the caller can see.
        /// </summary>
        public Task<TurnResult> RunTurnMessageAtOriginAsync(
            ChatMessage userInput, ConversationTurnOrigin? origin, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(userInput);

            return Stream.RunTurnCoreAsync(userInput, origin, cancellationToken);
        }

        /// <summary>Runs one turn and streams the reply as it arrives.</summary>
        /// <returns>The reply, one update at a time. Every update carries content.</returns>
        /// <exception cref="InvalidOperationException">
        /// The conversation already reached a terminal stage, another turn of this conversation still ran after the wait limit, or the
        /// stage the machine holds names no agent.
        /// </exception>
        public IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingAsync(
            string userInput, CancellationToken cancellationToken = default)
        {
            return RunTurnStreamingAtOriginAsync(userInput, origin: null, cancellationToken);
        }

        /// <summary>Runs one turn from a message the caller built and streams the reply as it arrives.</summary>
        /// <returns>The reply, one update at a time. Every update carries content.</returns>
        /// <exception cref="InvalidOperationException">
        /// The conversation already reached a terminal stage, another turn of this conversation still ran after the wait limit, or the
        /// stage the machine holds names no agent.
        /// </exception>
        public IAsyncEnumerable<ChatResponseUpdate> RunTurnMessageStreamingAsync(
            ChatMessage userInput, CancellationToken cancellationToken)
        {
            return RunTurnMessageStreamingAtOriginAsync(userInput, origin: null, cancellationToken);
        }

        /// <summary>Builds the answer message for one queued approval request of this conversation.</summary>
        public ChatMessage? TryCreateApprovalAnswer(string requestId, bool approved)
        {
            return Ledger.TryCreateApprovalAnswer(requestId, approved);
        }

        /// <summary>Builds the answer message for one queued approval request of a resumed conversation.</summary>
        /// <param name="requestId">The id of the pending request this answers.</param>
        /// <param name="approved">Whether the tool may run.</param>
        /// <param name="cancellationToken">Cancels the resume.</param>
        /// <returns>The user message carrying the approval response, or <see langword="null"/> when no queued request carries that id.</returns>
        public async ValueTask<ChatMessage?> TryCreateApprovalAnswerAsync(
            string requestId, bool approved, CancellationToken cancellationToken = default)
        {
            _ = await Ledger.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
            return Ledger.TryCreateApprovalAnswer(requestId, approved);
        }

        /// <summary>
        /// Runs one streaming turn that knows where it sits in the conversation the caller can see.
        /// </summary>
        public IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingAtOriginAsync(
            string userInput,
            ConversationTurnOrigin? origin,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(userInput);

            return Stream.RunTurnStreamingCoreAsync(
                new ChatMessage(ChatRole.User, userInput), origin, cancellationToken);
        }

        /// <summary>
        /// Runs one streaming turn from a message the caller built, that knows where it sits in the
        /// conversation the caller can see.
        /// </summary>
        public IAsyncEnumerable<ChatResponseUpdate> RunTurnMessageStreamingAtOriginAsync(
            ChatMessage userInput,
            ConversationTurnOrigin? origin,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(userInput);

            return Stream.RunTurnStreamingCoreAsync(userInput, origin, cancellationToken);
        }

        /// <inheritdoc />
        public Task<TurnRun> StartTurnAsync(
            ChatMessage userInput, ConversationTurnOrigin? origin, CancellationToken cancellationToken = default)
        {
            return Stream.StartTurnAsync(userInput, origin, before: [], cancellationToken);
        }

        /// <inheritdoc />
        public bool Cut(int turnIndex, TurnCut cut)
        {
            return Cuts.Cut(turnIndex, cut);
        }

        /// <inheritdoc />
        public bool Recut(int turnIndex, TurnCut cut)
        {
            return Cuts.Recut(turnIndex, cut);
        }

        /// <summary>Ends the conversation. <see cref="ConversationEnded"/> is raised now, or right after the running turn.</summary>
        public bool EndConversation(ConversationEndReason reason)
        {
            return Lifetime.EndConversation(reason);
        }

        /// <inheritdoc cref="IChannelControl.Send"/>
        public ChannelCommandResult Send(ChannelCommand command)
        {
            return Commands.Send(command);
        }

        /// <summary>
        /// Refuses every later turn, waits for a running one to end, and disposes this conversation's background sessions
        /// and shell executors. Idempotent: a session already disposed, or one that never had either, disposes nothing.
        /// </summary>
        public ValueTask DisposeAsync()
        {
            return Lifetime.DisposeAsync();
        }

        /// <summary>
        /// Waits for every message store write this conversation has queued.
        /// </summary>
        public Task FlushTranscriptAsync()
        {
            return Ledger.FlushTranscriptAsync();
        }

        /// <summary>
        /// Waits until every notice this conversation raised so far was handled by every hook, or abandoned at
        /// the hook's notice timeout.
        /// </summary>
        public Task FlushNoticesAsync()
        {
            return Hooks.FlushAsync();
        }
    }
}

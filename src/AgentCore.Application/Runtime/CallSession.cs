using System.Text.Json;
using AgentCore.Application.Calls;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Policy;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.State;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using AgentCore.Domain.Knowledge;
using AgentCore.Domain.Audit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The turn loop of one call. It owns the state, the stage machine, and the transcript.
/// </summary>
public sealed class CallSession : IConversationPort, IAsyncDisposable
{
    /// <summary>
    /// The line the caller hears when a turn fails and the document names none.
    /// </summary>
    public const string FallbackReply = AgentCoreConfiguration.DefaultFallbackReply;

    /// <summary>
    /// The reason a turn that produced no text reports.
    /// </summary>
    public const string EmptyReplyReason = "the run returned an empty reply, so the turn spoke the fallback.";

    /// <summary>
    /// The reason a turn that lost its tool budget reports, before the message of the fault.
    /// </summary>
    public const string ToolFailureReason = "a tool failed four times in a row, so the turn spoke the fallback.";

    /// <summary>
    /// The failure the turn records when the completion work passes its deadline.
    /// </summary>
    internal const string ExtractionTimedOutReason = "the turn completion passed its deadline.";

    /// <summary>
    /// What the log records when the moderation endpoint runs out of time.
    /// </summary>
    internal const string ModerationTimedOutReason = "the moderation endpoint passed its deadline.";

    /// <summary>
    /// What the log records when the moderation endpoint throws.
    /// </summary>
    internal const string ModerationFaultedReason = "the moderation endpoint threw.";

    /// <summary>
    /// How long the work after the reply may take before it is abandoned.
    /// </summary>
    internal static readonly TimeSpan TurnCompletionTimeout = TimeSpan.FromSeconds(5);

    private readonly CompiledAgent _compiled;

    private readonly StagePolicy? _policy;

    private readonly StateExtractor? _extractor;

    private readonly CounterStateWriter _counters;

    private readonly TimeProvider _time;

    private readonly CallEventChain _events;

    private readonly DateTimeOffset _startedAt;

    private readonly AgentCoreChatHistoryProvider _history;

    private readonly bool _sessionCarriesHistory;

    private readonly ILogger _logger;

    private readonly Lock _interruptLock = new();

    private readonly Clarifications _clarifications = new();

    private readonly CallWorkspace? _workspace;

    private readonly CallShells? _shells;

    // Phase 1 seam: one live-state owner, verb-split operators. The helpers below hold this
    // session and operate on the same fields under the same lock; only ownership of the verbs
    // moved, so every commit protocol is byte-identical to the single-file shape.
    internal StagePolicy? Policy => _policy;

    internal StateExtractor? Extractor => _extractor;

    internal CounterStateWriter Counters => _counters;

    internal TimeProvider Time => _time;

    internal CallEventChain Events => _events;

    internal DateTimeOffset StartedAt => _startedAt;

    internal AgentCoreChatHistoryProvider History => _history;

    internal bool SessionCarriesHistory => _sessionCarriesHistory;

    internal ILogger Logger => _logger;

    internal Lock InterruptLock => _interruptLock;

    internal CallShells? Shells => _shells;

    internal CallWorkspace? WorkspaceRoot => _workspace;

    internal AgentSession? AgentSession { get; set; }

    /// <summary>
    /// The call's shared workflow session on a graph row whose document declares a harness
    /// switch. MAF restores the participants' provider state from its checkpoints on every
    /// turn; any other call runs every turn fresh and leaves this <see langword="null"/>.
    /// </summary>
    internal AgentSession? GraphSession { get; set; }

    /// <summary>
    /// The last serialization of <see cref="GraphSession"/>, read back at resume. Refreshed
    /// at every turn end; a host snapshotting mid-turn gets the last turn boundary.
    /// </summary>
    internal JsonElement? GraphBlob { get; set; }

    internal CancellationTokenSource? RunCancellation { get; set; }

    internal CallInterruptionTracker.Interruption? Interruption { get; set; }

    internal Guid? AmendableEventId { get; set; }

    internal CallSessionState? Checkpoint { get; set; }
    
    internal Clarifications Clarifications => _clarifications;

    internal CallTurnRunner Runner { get; }

    internal CallTurnStream Stream { get; }

    internal CallTurnCompletion Completion { get; }

    internal CallTurnWriters Writers { get; }

    internal CallInterruptionTracker Interruptions { get; }
    
    internal CallTranscriptLedger Ledger { get; }

    internal CallSessionStateStore States { get; }
    
    internal CallSessionLifetime Lifetime { get; }

    /// <summary>
    /// Creates the session of one call.
    /// </summary>
    internal CallSession(
        string callId,
        CompiledAgent compiled,
        IGuardEvaluator guards,
        StateExtractor? extractor,
        TimeProvider timeProvider,
        CallObserverDispatcher? observers = null,
        ILogger? logger = null,
        CallWorkspace? workspace = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(guards);
        ArgumentNullException.ThrowIfNull(timeProvider);

        CallId = callId;

        _logger = logger ?? NullLogger.Instance;

        _workspace = workspace;

        _shells = workspace is null ? null : new CallShells(workspace.Path, _logger);

        _compiled = compiled;

        _history = compiled.History;

        _sessionCarriesHistory = compiled.SessionCarriesHistory;

        _extractor = extractor;

        _counters = new CounterStateWriter(guards);

        _time = timeProvider;

        // The seam is optional and it has a working default. A host that binds nothing to watch the
        // call still answers it, and the library never throws for want of an observer.
        _events = new CallEventChain(callId, observers ?? new CallObserverDispatcher([]), timeProvider);

        _startedAt = timeProvider.GetUtcNow();

        // An entry with no policy: has no stage machine. The single-agent row and both graph rows
        // read that way, and neither of them ever ends a call by itself.
        _policy = compiled.Policy is null ? null : compiled.CreatePolicy(guards);

        State = new StateDocument(compiled.Configuration, _policy?.Stage);

        // The writers run in a fixed order, and this is its only record: const slots land before
        // any turn, then each turn applies tool results, the extractor, the clock fields and the
        // counters, in CompleteTurnAsync. Guards read the finished document, so the order is
        // load-bearing.
        ConstStateWriter.Apply(State);

        // The first fact of this SESSION, and not necessarily of the call: a resumed call raises a
        // second one, behind the turns of the session before it. Nothing allocates a position here
        // any more — store 3 assigns the sequence — so a second one collides with nothing, and it is
        // raised rather than suppressed because a session picking the call up is a fact that
        // happened and the chain is where facts that happened go. Suppressing it is also not
        // available from here: knowing whether the call already has words needs the store read that
        // only OpenSessionAsync can do, and moving the raise there would let a call opened, never
        // spoken to, and then ended through EndCall write a call.ended with no call.started in front
        // of it.
        _ = _events.Raise(CallEventKind.CallStarted, _startedAt, turnIndex: null);

        Runner = new CallTurnRunner(this);
        Stream = new CallTurnStream(this);
        Completion = new CallTurnCompletion(this);
        Writers = new CallTurnWriters(this);
        Interruptions = new CallInterruptionTracker(this);
        Ledger = new CallTranscriptLedger(this);
        States = new CallSessionStateStore(this);
        Lifetime = new CallSessionLifetime(this);
    }

    /// <summary>
    /// Gets the id of the call.
    /// </summary>
    public string CallId { get; }

    /// <summary>
    /// Gets the folder this call owns on disk, or <see langword="null"/> when the host bound no
    /// workspace root. The folder is deleted when the call ends.
    /// </summary>
    public string? Workspace => _workspace?.Path;

    /// <summary>
    /// Gets the stage the machine holds. It is empty when the entry declares no policy.
    /// </summary>
    public string Stage => State.Stage;

    /// <summary>
    /// Gets whether the call reached a terminal stage. An entry with no policy never does.
    /// </summary>
    public bool IsComplete { get; internal set; }

    /// <summary>
    /// Gets the state of this call. Every guard and every increment rule reads it.
    /// </summary>
    public StateDocument State { get; }

    /// <summary>
    /// Gets or sets the knowledge scope the host opened for this call, or null for none. Set it
    /// before the run; every turn composes its own scope from it.
    /// </summary>
    public KnowledgeScope? Scope { get; set; }

    /// <summary>
    /// Gets the conversation, oldest first. Every stage of the call reads it.
    /// </summary>
    public IReadOnlyList<ChatMessage> Transcript
        => Ledger.Session() is { } session ? _history.Read(session) : [];

    /// <summary>
    /// Gets the turn that finished last, or <see langword="null"/> before the first turn ends.
    /// </summary>
    public TurnResult? LastTurn { get; internal set; }

    /// <summary>
    /// Gets the name the last written message was stored under, for a caller to hang an edit off.
    /// </summary>
    public string? LastReplyMessageId { get; internal set; }

    /// <summary>
    /// Gets the compiled agent this session runs. Every call shares it.
    /// </summary>
    public CompiledAgent Compiled => _compiled;

    /// <summary>
    /// Whether this call keeps one workflow session across turns: a graph row whose document
    /// declares a harness switch. Everything else either carries history on a long-lived
    /// session already (rows 1 and 2) or keeps no provider state at all.
    /// </summary>
    internal bool ReusesGraphSession => !_sessionCarriesHistory && _compiled.HarnessStateKeys.Count > 0;

    /// <summary>
    /// Gives the runs this call delegates through one tool a set of tools of their own.
    /// </summary>
    public void SetDelegatedTools(string delegatingToolId, IReadOnlyList<AITool> tools)
        => Runner.SetDelegatedTools(delegatingToolId, tools);

    /// <summary>
    /// Gives this call the screen its tools draw on, or takes it away.
    /// </summary>
    public void SetHasScreen(bool hasScreen) => Runner.SetHasScreen(hasScreen);

    /// <summary>
    /// Runs one turn end to end, and returns what it did.
    /// </summary>
    public Task<TurnResult> RunTurnAsync(string userInput, CancellationToken cancellationToken = default)
        => RunTurnAtOriginAsync(userInput, origin: null, cancellationToken);

    /// <summary>
    /// Runs one turn end to end from a message the caller built, and returns what it did.
    /// </summary>
    public Task<TurnResult> RunTurnMessageAsync(ChatMessage userInput, CancellationToken cancellationToken)
        => RunTurnMessageAtOriginAsync(userInput, origin: null, cancellationToken);

    /// <summary>
    /// Runs one turn that knows where it sits in the conversation the caller can see.
    /// </summary>
    public Task<TurnResult> RunTurnAtOriginAsync(
        string userInput, CallTurnOrigin? origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userInput);

        return Runner.RunTurnCoreAsync(new ChatMessage(ChatRole.User, userInput), origin, cancellationToken);
    }

    /// <summary>
    /// Runs one turn from a message the caller built, that knows where it sits in the conversation
    /// the caller can see.
    /// </summary>
    public Task<TurnResult> RunTurnMessageAtOriginAsync(
        ChatMessage userInput, CallTurnOrigin? origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userInput);

        return Runner.RunTurnCoreAsync(userInput, origin, cancellationToken);
    }

    /// <summary>Runs one turn and streams the reply as it arrives.</summary>
    /// <returns>The reply, one update at a time. Every update carries content.</returns>
    /// <exception cref="InvalidOperationException">
    /// The call already reached a terminal stage, another turn of this call is still running, or the
    /// stage the machine holds names no agent.
    /// </exception>
    public IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingAsync(
        string userInput, CancellationToken cancellationToken = default)
        => RunTurnStreamingAtOriginAsync(userInput, origin: null, cancellationToken);

    /// <summary>Runs one turn from a message the caller built and streams the reply as it arrives.</summary>
    /// <returns>The reply, one update at a time. Every update carries content.</returns>
    /// <exception cref="InvalidOperationException">
    /// The call already reached a terminal stage, another turn of this call is still running, or the
    /// stage the machine holds names no agent.
    /// </exception>
    public IAsyncEnumerable<ChatResponseUpdate> RunTurnMessageStreamingAsync(
        ChatMessage userInput, CancellationToken cancellationToken)
        => RunTurnMessageStreamingAtOriginAsync(userInput, origin: null, cancellationToken);

    /// <summary>Builds the answer message for one queued approval request of this call.</summary>
    public ChatMessage? TryCreateApprovalAnswer(string requestId, bool approved)
        => Ledger.TryCreateApprovalAnswer(requestId, approved);

    /// <summary>Builds the answer message for one queued approval request of a resumed call.</summary>
    /// <param name="requestId">The id of the pending request this answers.</param>
    /// <param name="approved">Whether the tool may run.</param>
    /// <param name="cancellationToken">Cancels the resume.</param>
    /// <returns>The user message carrying the approval response, or <see langword="null"/> when no queued request carries that id.</returns>
    public async ValueTask<ChatMessage?> TryCreateApprovalAnswerAsync(
        string requestId, bool approved, CancellationToken cancellationToken = default)
    {
        await Ledger.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        return Ledger.TryCreateApprovalAnswer(requestId, approved);
    }

    /// <summary>
    /// Runs one streaming turn that knows where it sits in the conversation the caller can see.
    /// </summary>
    public IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingAtOriginAsync(
        string userInput,
        CallTurnOrigin? origin,
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
        CallTurnOrigin? origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userInput);

        return Stream.RunTurnStreamingCoreAsync(userInput, origin, cancellationToken);
    }

    /// <summary>
    /// Ends the running turn where the caller cut the reply off.
    /// </summary>
    public bool Interrupt(
        string utteranceUntilInterrupt,
        TimeSpan durationUntilInterrupt,
        bool cutsRunningTurn = true)
        => Interruptions.Interrupt(utteranceUntilInterrupt, durationUntilInterrupt, cutsRunningTurn);

    /// <summary>Closes the call, and writes the last event of its chain.</summary>
    public bool EndCall(CallEndReason reason) => Lifetime.EndCall(reason);

    /// <summary>
    /// Disposes this call's background sessions and shell executors. Idempotent: a session already
    /// disposed, or one that never had either, disposes nothing.
    /// </summary>
    public ValueTask DisposeAsync() => Lifetime.DisposeAsync();

    /// <summary>
    /// Waits for every store 1 write this call has queued.
    /// </summary>
    public Task FlushTranscriptAsync() => Ledger.FlushTranscriptAsync();

    /// <summary>
    /// Reads the state this call would resume from, as it stands right now. The provider state is
    /// read off the live bag with no turn lock around it, so a host serializing mid-turn gets the
    /// bag as it stands at that instant, not a turn-boundary snapshot. A graph row that reuses
    /// its session instead attaches that session's last turn-end serialization.
    /// </summary>
    internal CallSessionState Snapshot() => States.Snapshot();

    /// <summary>Names the state this call resumes from when store 0 holds none of its own.</summary>
    internal void Resume(CallSessionState stored) => States.Resume(stored);
}

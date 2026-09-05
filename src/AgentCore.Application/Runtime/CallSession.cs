using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using AgentCore.Application.Calls;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Policy;
using AgentCore.Application.Ports;
using AgentCore.Application.State;
using AgentCore.Application.Tools;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The turn loop of one call. It owns the state, the stage machine, and the transcript.
/// </summary>
public sealed class CallSession : IConversationPort
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
    private static readonly TimeSpan TurnCompletionTimeout = TimeSpan.FromSeconds(5);

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

    private AgentSession? _agentSession;

    private CancellationTokenSource? _runCancellation;

    private Interruption? _interruption;

    private Guid? _amendableEventId;

    private int _running;

    private CallSessionState? _checkpoint;

    private readonly Clarifications _clarifications = new();

    private readonly IReadOnlyDictionary<string, VocabularyView> _vocabulary;

    // Whether the running turn has already handed the host something to speak. One rule for both
    // run shapes: a run that has handed the host nothing cannot be the turn the caller was hearing,
    // so a barge-in in that window belongs to the turn that finished before it. A streaming turn
    // raises this at its first piece of content; a turn that never streams hands the host nothing
    // until it returns, so it never raises it and is never cut in flight. It is dropped twice:
    // once in CompleteTurnAsync's commit lock, the moment the turn's own record exists for a late
    // barge-in to amend, and again in EndRun, which is the only clear a turn that never reached
    // that commit gets.
    private volatile bool _runIsAudible;

    private (string ToolId, IReadOnlyList<AITool> Tools)? _delegatedTools;

    private bool _hasScreen;

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
        VocabularyCache? vocabulary = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(guards);
        ArgumentNullException.ThrowIfNull(timeProvider);

        CallId = callId;

        _logger = logger ?? NullLogger.Instance;

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

        // A document with no policy: has no stage machine. The single-agent row and both graph rows
        // read that way, and neither of them ever ends a call by itself.
        _policy = compiled.Configuration.Policy is null ? null : compiled.CreatePolicy(guards);

        // Sampled once, here, and handed to both the gate and the linker (K40): a refresh landing
        // mid-call must not let the two sides of one write disagree about what the vocabulary was.
        _vocabulary = vocabulary?.Snapshot() ?? new Dictionary<string, VocabularyView>(StringComparer.Ordinal);

        State = new StateDocument(compiled.Configuration, _policy?.Stage, _vocabulary);

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
    }

    /// <summary>
    /// Gets the id of the call.
    /// </summary>
    public string CallId { get; }

    /// <summary>
    /// Gets the stage the machine holds. It is empty when the document declares no policy.
    /// </summary>
    public string Stage => State.Stage;

    /// <summary>
    /// Gets whether the call reached a terminal stage. A document with no policy never does.
    /// </summary>
    public bool IsComplete { get; private set; }

    /// <summary>
    /// Gets the state of this call. Every guard and every increment rule reads it.
    /// </summary>
    public StateDocument State { get; }

    /// <summary>
    /// Gets the conversation, oldest first. Every stage of the call reads it.
    /// </summary>
    public IReadOnlyList<ChatMessage> Transcript
        => Session() is { } session ? _history.Read(session) : [];

    /// <summary>
    /// Gets the turn that finished last, or <see langword="null"/> before the first turn ends.
    /// </summary>
    public TurnResult? LastTurn { get; private set; }

    /// <summary>
    /// Gets the name the last written message was stored under, for a caller to hang an edit off.
    /// </summary>
    public string? LastReplyMessageId { get; private set; }

    /// <summary>
    /// Gets the compiled agent this session runs. Every call shares it.
    /// </summary>
    public CompiledAgent Compiled => _compiled;

    /// <summary>
    /// Gives the runs this call delegates through one tool a set of tools of their own.
    /// </summary>
    /// <param name="delegatingToolId">The <c>kind: agent</c> tool whose delegated runs are offered these.</param>
    /// <param name="tools">The tools. An empty list offers nothing, exactly as never calling this does.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public void SetDelegatedTools(string delegatingToolId, IReadOnlyList<AITool> tools)
    {
        ArgumentNullException.ThrowIfNull(delegatingToolId);
        ArgumentNullException.ThrowIfNull(tools);

        _delegatedTools = (delegatingToolId, tools);
    }

    /// <summary>
    /// Gives this call the screen its tools draw on, or takes it away.
    /// </summary>
    public void SetHasScreen(bool hasScreen) => _hasScreen = hasScreen;

    /// <summary>
    /// Runs one turn end to end, and returns what it did.
    /// </summary>
    public Task<TurnResult> RunTurnAsync(string userInput, CancellationToken cancellationToken = default)
        => RunTurnAtOriginAsync(userInput, origin: null, cancellationToken);

    /// <summary>
    /// Runs one turn that knows where it sits in the conversation the caller can see.
    /// </summary>
    /// <param name="userInput">What the caller said.</param>
    /// <param name="origin">
    /// Where these words hang, or <see langword="null"/> for a caller that does not track its
    /// messages by name. See <see cref="CallTurnOrigin"/>: supplying it is what lets a caller send an
    /// earlier message again and have the answers to the old one withdrawn.
    /// </param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    public async Task<TurnResult> RunTurnAtOriginAsync(
        string userInput, CallTurnOrigin? origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userInput);

        var session = await OpenSessionAsync(cancellationToken).ConfigureAwait(false);

        var turn = BeginTurn(userInput, session, origin);

        var cancellation = StartRun(cancellationToken);

        try
        {
            using var ambients = EnterAmbients(turn);

            AgentResponse response;
            string? toolFault = null;

            try
            {
                // Interrupt never cancels this token: the turn is not audible, so a barge-in takes
                // the amendment path against the turn that finished last. Only the host's own token
                // cancels this run, and that cancellation propagates.
                response = await turn.Agent
                    .RunAsync(
                        turn.Request,
                        await RunSessionAsync(turn, cancellation.Token).ConfigureAwait(false),
                        options: null,
                        cancellationToken: cancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Section 8.7, sixth row. The run throws, the turn ends, and the call lives.
                toolFault = exception.Message;
                response = new AgentResponse();
            }

            // CompleteTurnAsync publishes LastTurn itself, under the same lock that records what a
            // late barge-in may still amend. Assigning it a second time here could overwrite an
            // amendment that landed in between.
            return await CompleteTurnAsync(turn, response, toolFault, ReadDisposition(response), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            EndRun(cancellation);
            turn.Activity?.Dispose();
        }
    }

    /// <summary>Runs one turn and streams the reply as it arrives.</summary>
    /// <param name="userInput">What the caller said.</param>
    /// <param name="cancellationToken">Cancels the model calls.</param>
    /// <returns>The reply, one update at a time. Every update carries content.</returns>
    /// <exception cref="InvalidOperationException">
    /// The call already reached a terminal stage, another turn of this call is still running, or the
    /// stage the machine holds names no agent.
    /// </exception>
    public IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingAsync(
        string userInput, CancellationToken cancellationToken = default)
        => RunTurnStreamingAtOriginAsync(userInput, origin: null, cancellationToken);

    /// <summary>
    /// Runs one streaming turn that knows where it sits in the conversation the caller can see.
    /// </summary>
    /// <param name="userInput">What the caller said.</param>
    /// <param name="origin">
    /// Where these words hang, or <see langword="null"/> for a caller that does not track its
    /// messages by name. See <see cref="RunTurnAtOriginAsync"/>, which this mirrors.
    /// </param>
    /// <param name="cancellationToken">Cancels the model calls.</param>
    /// <returns>The reply, one update at a time. Every update carries content.</returns>
    public async IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingAtOriginAsync(
        string userInput,
        CallTurnOrigin? origin,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userInput);

        var session = await OpenSessionAsync(cancellationToken).ConfigureAwait(false);

        var turn = BeginTurn(userInput, session, origin);

        // A streaming turn becomes audible only once it hands the host its first piece of content.
        // Until then nothing of it has reached the caller, and a barge-in belongs elsewhere.
        var cancellation = StartRun(cancellationToken);
        try
        {
            List<AgentResponseUpdate> updates = [];
            string? toolFault = null;

            // This scope covers opening the run's session and building the stream, and reaches no
            // round: an async iterator restores its caller's execution context at every yield.
            using var opening = EnterAmbients(turn);

            var stream = ScopedEnumerator.Over(
                turn.Agent
                    .RunStreamingAsync(
                        turn.Request,
                        await RunSessionAsync(turn, cancellation.Token).ConfigureAwait(false),
                        options: null,
                        cancellationToken: cancellation.Token)
                    .GetAsyncEnumerator(cancellation.Token),
                () => EnterAmbients(turn));

            try
            {
                while (true)
                {
                    AgentResponseUpdate update;

                    // The enumeration itself sits in its own try, because a method that yields takes
                    // no catch clause around the yield.
                    try
                    {
                        if (!await stream.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        update = stream.Current;
                    }
                    catch (OperationCanceledException) when (CurrentInterruption() is not null)
                    {
                        // The caller spoke over the reply, and the relay already reported how much of
                        // it played. Nothing here estimates that value: see item 6c.
                        break;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // Section 8.7, sixth row.
                        toolFault = exception.Message;
                        break;
                    }

                    updates.Add(update);

                    var content = update.AsChatResponseUpdate();
                    if (TurnMessages.CarriesContent(content) && Speaks(update))
                    {
                        // Marked before the yield, not after it: an async iterator only resumes past
                        // a yield once the host comes back for the next update, and by then the host
                        // has already queued this piece for the caller.
                        _runIsAudible = true;

                        // The host speaks this, so it leaves the seam as fast as the model produced it.
                        yield return content;
                    }
                }
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            // CompleteTurnAsync publishes LastTurn itself. See the same note in RunTurnAsync. The
            // disposition comes off the raw updates and never off the folded response: update-level
            // properties do not survive streaming coalescing.
            _ = await CompleteTurnAsync(
                    turn, updates.ToAgentResponse(), toolFault, ReadDisposition(updates), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            EndRun(cancellation);
            turn.Activity?.Dispose();
        }
    }

    /// <summary>
    /// Ends the running turn where the caller cut the reply off.
    /// </summary>
    /// <param name="utteranceUntilInterrupt">The text the caller actually heard.</param>
    /// <param name="durationUntilInterrupt">How much of the reply played, as the relay reported it.</param>
    /// <param name="cutsRunningTurn">
    /// Whether the turn running now is the turn the caller was hearing. An adapter that tracks which
    /// turn's output actually reached the vendor answers that question itself and says so here;
    /// <see langword="false"/> sends the frame straight to the amendment path, whatever the running
    /// turn has produced. The default is <see langword="true"/>, which leaves the decision to this
    /// session alone, and that is the right answer for a caller that speaks over a reply it is
    /// itself streaming.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when this call recorded the barge-in, either by ending the running
    /// turn or by amending the turn that finished last, and <see langword="false"/> when there was
    /// nothing to record it against.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="durationUntilInterrupt"/> is negative.
    /// </exception>
    public bool Interrupt(
        string utteranceUntilInterrupt,
        TimeSpan durationUntilInterrupt,
        bool cutsRunningTurn = true)
    {
        ArgumentNullException.ThrowIfNull(utteranceUntilInterrupt);
        ArgumentOutOfRangeException.ThrowIfLessThan(durationUntilInterrupt, TimeSpan.Zero);

        lock (_interruptLock)
        {
            // Both conditions, and in this order. The adapter's answer only ever removes the
            // in-flight path: an adapter that knows the caller was hearing an earlier turn skips it
            // outright, and one that says nothing leaves this session's own audibility test to
            // decide.
            if (cutsRunningTurn && _runCancellation is { } cancellation && _runIsAudible)
            {
                _interruption = new Interruption(utteranceUntilInterrupt, durationUntilInterrupt);
                cancellation.Cancel();
                return true;
            }

            return AmendLastTurn(new Interruption(utteranceUntilInterrupt, durationUntilInterrupt));
        }
    }

    /// <summary>Closes the call, and writes the last event of its chain.</summary>
    /// <param name="reason">Why the call ended, as one member of the closed set.</param>
    /// <returns>
    /// <see langword="true"/> when this call wrote the event, and <see langword="false"/> when the
    /// call had already ended.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="reason"/> is not a member of the closed set.
    /// </exception>
    public bool EndCall(CallEndReason reason)
    {
        // The event is written before the flag moves. A value outside the closed set therefore ends
        // no call and writes nothing.
        var wrote = _events.EndCall(reason, _time.GetUtcNow());
        IsComplete = true;
        return wrote;
    }

    /// <summary>
    /// Opens the session of this call, once, and hands the same one to every later turn.
    /// </summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The session.</returns>
    private async ValueTask<AgentSession> OpenSessionAsync(CancellationToken cancellationToken)
    {
        if (Session() is { } opened)
        {
            return opened;
        }

        var session = _sessionCarriesHistory
            ? await _compiled.TurnAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false)
            : new CallHistorySession();

        var record = await _compiled.CallStore.CreateAsync(CallId, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<CallMessage> spoken = record.LastMessageAt is null
            ? []
            : await _compiled.CallStore.ReadAsync(CallId, cancellationToken).ConfigureAwait(false);

        lock (_interruptLock)
        {
            if (_agentSession is null)
            {
                _agentSession = session;

                State.TurnIndex = _history.BeginCall(
                    session, CallId, spoken, _events.RaiseDroppedTranscriptWrite, record.State ?? _checkpoint);

                // After the constructor, never inside it. The const writer has already run by now,
                // so a slot a previous session filled lands on top of the const default rather than
                // under it — and the record it is read from only exists after an async store read.
                //
                // One restore point for both sources, and store 0 outranks the host's checkpoint
                // outright rather than merging with it. Store 0's blob is written in the same batch
                // as the turn's words, so its state and store 1's words are of one moment and cannot
                // disagree; a checkpoint's state beside store 1's words can be of two. So the
                // checkpoint decides only a call store 0 does not know, or knows without state.
                if ((record.State ?? _checkpoint) is { } stored)
                {
                    Restore(stored);
                }
            }

            return _agentSession;
        }
    }

    /// <summary>Reads the state this call would resume from, as it stands right now.</summary>
    internal CallSessionState Snapshot()
    {
        lock (_interruptLock)
        {
            if (_agentSession is null && _checkpoint is { } held)
            {
                // Handed back by reference, where the branch below deep-clones through
                // WrittenSlots(). Deliberate, and not the asymmetry it looks like: this value is the
                // host's own object, arriving from Resume and going straight back out to the only
                // caller that can reach this branch — the seam, which serializes it and drops it. A
                // clone would defend the host against itself, and cost a copy of the slots to do it.
                return held;
            }
        }

        return new()
        {
            Stage = State.Stage,
            IsComplete = IsComplete,
            Slots = State.WrittenSlots(),

            // The turn index this call has reached, which is already the NEXT one by the time the
            // commit reads it.
            NextTurnIndex = State.TurnIndex,

            // Read here so a snapshot taken outside a turn — the serialize seam — carries it. On the
            // commit path the provider overwrites it, because the ordinal this turn leaves behind is
            // not settled until the turn's rows are cut.
            NextOrdinal = Session() is { } session ? _history.NextOrdinal(session) : 0,

            Clarifications = _clarifications.Spent(),
        };
    }

    /// <summary>Names the state this call resumes from when store 0 holds none of its own.</summary>
    internal void Resume(CallSessionState stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        // One lock over the check and the write. The guard exists to catch a late hand-off, and a
        // guard that read the session under the lock and then wrote the field outside it would be
        // racing the very reader — OpenSessionAsync at the top of this file — that it guards.
        lock (_interruptLock)
        {
            if (_agentSession is not null)
            {
                throw new InvalidOperationException(
                    $"The call '{CallId}' has already run a turn, and a call resumes only as its "
                    + "first turn opens it. Hand the state to the factory that builds the session "
                    + "instead.");
            }

            _checkpoint = stored;
        }
    }

    /// <summary>Puts back what a previous session of this call had, as far as the document still allows.</summary>
    private void Restore(CallSessionState stored)
    {
        if (stored.Version != CallSessionState.CurrentVersion)
        {
            Dropped($"the stored state is version {stored.Version} and this build writes {CallSessionState.CurrentVersion}.");
            return;
        }

        string? refusedStage = null;

        if (_policy is null)
        {
            // A document with no policy: has no stage machine and no stage to hold, so the only
            // stored stage it can honour is no stage at all. An id from a build that still declared
            // policy: would otherwise land in the reserved stage slot, where the guards and the
            // audit chain read it as though a machine were holding it.
            if (stored.Stage.Length > 0)
            {
                refusedStage = $"the document declares no policy, so the stage '{stored.Stage}' has nowhere to go.";
            }
        }
        else if (!_policy.Declares(stored.Stage))
        {
            refusedStage = $"the document no longer declares the stage '{stored.Stage}'.";
        }

        if (refusedStage is null)
        {
            // Both, and in this order. The machine is what picks the agent and what the next
            // transition fires from; the reserved slot is what the guards and the audit chain read.
            // Moving one without the other is worse than moving neither, because the call would
            // then report a stage it was not actually running in.
            _policy?.RestoreStage(stored.Stage);
            State.Stage = stored.Stage;

            // Only on this branch. A stored 'true' was read off a terminal stage, so restoring it
            // beside a stage that was refused would bring the call back only to have it turn every
            // turn away — the one outcome this whole method exists to avoid.
            IsComplete = stored.IsComplete;
        }
        else
        {
            Dropped(refusedStage);
        }

        // Before the slots, and unconditionally: the ask budget is per call, not per session, so a
        // caller who dropped and reconnected must not buy a fresh maxAsks and hear every clarification
        // over again. A refused stage does not refuse this — the questions were still asked.
        _clarifications.RestoreSpent(stored.Clarifications);

        foreach (var slot in stored.Slots)
        {
            if (ReservedStateSlots.Contains(slot.Key))
            {
                // TryWrite throws on a reserved slot rather than answering false, and an exception
                // here would escape OpenSessionAsync and refuse the call outright. Snapshot never
                // writes one, but this blob is arbitrary JSON out of store 0 and a host hands one
                // straight in through DeserializeSessionAsync, so the guard is the caller's and not
                // the blob's.
                Dropped($"the slot '{slot.Key}' is reserved, and a reserved slot is never restored.");
                continue;
            }

            if (State.TryWrite(slot.Key, slot.Value?.DeepClone()))
            {
                continue;
            }

            // TryWrite answers false for two kinds of reason that cost an operator different things
            // to fix — a slot the document no longer declares, and a value its type, enum: or
            // vocabulary: gate now refuses — so the reason says which one happened rather than
            // making them guess.
            Dropped(
                State.Configuration.State.ContainsKey(slot.Key)
                    ? $"the slot '{slot.Key}' no longer takes the value it was stored with."
                    : $"the document no longer declares the slot '{slot.Key}'.");
        }

        void Dropped(string reason) => _events.RaiseDiagnostic(
            CallEventKind.StateRestorePartial,
            _time.GetUtcNow(),
            turnIndex: null,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CallEventPayloadKeys.Reason] = reason,
            });
    }

    /// <summary>
    /// Reads the session one run is handed.
    /// </summary>
    /// <param name="turn">The turn about to run.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The call's session on rows 1 and 2, and a fresh workflow session on a graph row.</returns>
    private async ValueTask<AgentSession> RunSessionAsync(Turn turn, CancellationToken cancellationToken)
        => _sessionCarriesHistory
            ? turn.Session
            : await turn.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Opens the ambients this turn runs under.
    /// </summary>
    /// <param name="turn">The turn about to run.</param>
    /// <returns>The scope. Disposing it closes every ambient it opened.</returns>
    private IDisposable EnterAmbients(Turn turn)
        => TurnAmbients.Enter(
            State,
            turn.Renders,
            turn.Sources,
            failure => _events.RaiseToolFailure(turn.Index, failure),
            TurnContextOf(turn),
            turn.Knowledge,
            _clarifications);

    /// <summary>
    /// Reads what one turn adds to its own model invocation.
    /// </summary>
    /// <param name="turn">The turn about to run.</param>
    /// <returns>The context <see cref="TurnContextProvider"/> merges into the request.</returns>
    private TurnContext TurnContextOf(Turn turn)
        => new()
        {
            Session = turn.Session,
            Instructions = turn.Reminder,
            Tools = _delegatedTools?.Tools,
            ToolsFor = _delegatedTools?.ToolId,
            CarriesHistory = _sessionCarriesHistory,
        };

    /// <summary>
    /// Reads the session of this call, or null before its first turn opened one.
    /// </summary>
    /// <returns>The session.</returns>
    private AgentSession? Session()
    {
        lock (_interruptLock)
        {
            return _agentSession;
        }
    }

    /// <summary>
    /// Waits for every store 1 write this call has queued.
    /// </summary>
    /// <returns>A task that completes when the words of the call are durable.</returns>
    public async Task FlushTranscriptAsync()
    {
        if (Session() is { } session)
        {
            await _history.DrainAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Picks the agent, withdraws whatever this turn replaces, builds the model input, and takes the
    /// turn.
    /// </summary>
    /// <param name="userInput">What the caller said.</param>
    /// <param name="session">The session of this call.</param>
    /// <param name="origin">Where the turn hangs, or null for a caller that does not say.</param>
    /// <returns>Everything the rest of the turn needs.</returns>
    private Turn BeginTurn(string userInput, AgentSession session, CallTurnOrigin? origin = null)
    {
        if (IsComplete)
        {
            throw new InvalidOperationException(
                $"The call '{CallId}' reached the terminal stage '{Stage}', so it runs no further turn.");
        }

        var agent = ResolveAgent();

        // One session runs one turn at a time. The state document takes no lock, so a second turn
        // that overlapped the first would corrupt it rather than fail.
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            throw new InvalidOperationException(
                $"A turn of the call '{CallId}' is still running. One call runs one turn at a time.");
        }

        Activity? activity = null;
        try
        {
            // After both guards: a turn refused as terminal or already-running must not clear the
            // per-turn mark out from under the turn actually in flight. Runs exactly once here, and
            // never in EnterAmbients, which reopens per streaming step and would clear the mark
            // several times inside one streaming turn (K41).
            _clarifications.BeginTurn();

            // Behind both guards, because the withdrawal deletes: a turn refused for a terminal call or
            // for one already running must not have taken the tail of the call with it on the way out.
            // Ahead of the request below, because the words it withdraws have to be gone from the live
            // history this run reads and not only from the store. The turn index is deliberately not
            // wound back with them: store 3 keeps its rows for the withdrawn turns, and two turns at one
            // index in the chain is worse than a gap in it.
            WithdrawSuperseded(session, origin);

            // The reminder rides a request that happens anyway, and it rides exactly one, as a message
            // the framework appends for that invocation and stores nowhere. It reads the state document
            // and never the transcript. Only a document with a policy: has a stage that waits on a slot.
            var reminder = _policy is null ? null : UnfilledSlotReminder.Build(State, _policy.CurrentStage);
            ChatMessage spoken = new(ChatRole.User, userInput);

            // The framework has never heard of a turn, so the turn loop names this one before the run.
            _history.BeginTurn(session, State.TurnIndex);

            // Rows 1 and 2 read the call out of the session, so the run carries the new message alone
            // and the provider prepends the rest. A workflow takes no provider, so its history rides
            // the request, rendered into the one role a node still recognises. Neither shape puts the
            // caller's message in store 1 yet: the turn writes what it said and what it heard together,
            // when it commits, so the run that is about to read the history does not find its own
            // prompt already in it.
            List<ChatMessage> request = _sessionCarriesHistory
                ? [spoken]
                : TurnMessages.GraphHistory(_history.Read(session)) is { } rendered ? [rendered, spoken] : [spoken];

            // One turn is one span. The call id rides here, on a span attribute, because T61 refuses it
            // on a metric. The span is disposed in the finally of whichever run method opened the turn.
            activity = AgentCoreTelemetry.StartTurn(CallId, State.TurnIndex, State.Stage);

            var knowledge = StateKnowledgeScope.Compose(
                State, _compiled.Configuration.Providers?.Knowledge?.Scope, KnowledgeScopeScope.Current);

            if (knowledge is { Origins.Count: > 0 })
            {
                Log.KnowledgeScopeComposed(_logger, CallId, State.TurnIndex, knowledge.Origins);
            }

            return new Turn(
                agent,
                session,
                request,
                spoken,
                reminder,
                State.Stage,
                State.TurnIndex,
                activity,
                _time.GetTimestamp(),
                _hasScreen ? new TurnRenders() : null,
                new TurnSources(),
                knowledge,
                origin?.MessageId);
        }
        catch
        {
            // Only the run methods' own finally frees the session and closes the span, and it starts
            // after this method returns. A throw while the turn is still being built would otherwise
            // leave _running set, so every later turn of the call is refused as one already running.
            activity?.Dispose();
            Volatile.Write(ref _running, 0);
            throw;
        }
    }

    /// <summary>Takes back everything the call said after the message this turn hangs off.</summary>
    private void WithdrawSuperseded(AgentSession session, CallTurnOrigin? origin)
    {
        if (origin is not { NamesParent: true }
            || _history.TruncateFrom(session, origin.ParentMessageId) is not { } withdrawn)
        {
            return;
        }

        // Without this, lastNamed and the pending list would survive a withdrawal that deleted the
        // very turns they recorded, and silence both ambiguity channels forever about a question the
        // caller edited away. The ask counters are deliberately untouched here: what the caller
        // heard, they still heard, and clearing them would let the withdrawn segment buy a fresh
        // maxAsks budget.
        _clarifications.Withdraw();

        // The turn index the event is filed under is the one about to run; the payload is what says
        // which turns it replaced. The rows of those turns are already deleted, so nothing else in
        // any store can answer that afterwards.
        _ = _events.Raise(
            CallEventKind.TurnSuperseded,
            _time.GetUtcNow(),
            State.TurnIndex,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.WithdrewFromTurnIndex] =
                    withdrawn.First.ToString(CultureInfo.InvariantCulture),
                [AuditPayloadKeys.WithdrewThroughTurnIndex] =
                    withdrawn.Last.ToString(CultureInfo.InvariantCulture),
            });
    }

    /// <summary>
    /// Opens the window in which <see cref="Interrupt"/> reaches this turn.
    /// </summary>
    /// <param name="cancellationToken">The token of the host.</param>
    /// <returns>The source the run reads. The caller ends it with <see cref="EndRun"/>.</returns>
    private CancellationTokenSource StartRun(CancellationToken cancellationToken)
    {
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (_interruptLock)
        {
            _interruption = null;
            _runCancellation = cancellation;
            _runIsAudible = false;
        }

        return cancellation;
    }

    /// <summary>
    /// Closes the window, and frees the session for the next turn.
    /// </summary>
    /// <param name="cancellation">The source <see cref="StartRun"/> returned.</param>
    private void EndRun(CancellationTokenSource cancellation)
    {
        // The field drops first. A frame that arrives now meets no source to cancel, so it reaches
        // the amendment path rather than a disposed one.
        lock (_interruptLock)
        {
            _runCancellation = null;
            _runIsAudible = false;
        }

        cancellation.Dispose();
        Volatile.Write(ref _running, 0);
    }

    /// <summary>
    /// Records a barge-in against the turn that finished last, and corrects its record.
    /// </summary>
    /// <param name="cut">What the relay reported: the heard text and the played duration.</param>
    /// <returns>
    /// <see langword="true"/> when the turn was amended, and <see langword="false"/> when there was
    /// no turn to amend.
    /// </returns>
    private bool AmendLastTurn(Interruption cut)
    {
        // The chain has already closed, so nothing may be appended behind call.ended.
        if (_events.HasEnded
            || _amendableEventId is not { } completedEventId
            || LastTurn is not { InterruptedAfter: null } finished
            || Session() is not { } session)
        {
            return false;
        }

        var heard = cut.HeardText.Trim();

        // Store 1 keeps the reply row and rewrites its words, rather than replacing the turn's
        // messages. The turn it corrects ran to its end, so every tool call it made is already
        // paired and there is nothing unfinished to strip out.
        //
        // The cut always lands today: a turn is amendable only when it completed with words, and a
        // turn that completed with words left a reply row holding them. The answer is still read,
        // because the row below names a HASH of those words — a chain that proves words store 1
        // does not hold proves nothing, so no cut means no amendment.
        if (!_history.TruncateLastReply(session, heard, cut.PlayedDuration))
        {
            return false;
        }

        LastTurn = finished with { ReplyText = heard, InterruptedAfter = cut.PlayedDuration };

        _events.RaiseReplyInterrupted(
            finished.TurnIndex, _time.GetUtcNow(), completedEventId, heard, cut.PlayedDuration);

        return true;
    }

    /// <summary>Reads the interruption the relay reported for the running turn.</summary>
    /// <returns>The record, or <see langword="null"/> when the caller did not interrupt.</returns>
    private Interruption? CurrentInterruption()
    {
        lock (_interruptLock)
        {
            return _interruption;
        }
    }

    /// <summary>Reads whether the caller hears the node that produced one update.</summary>
    /// <param name="update">One update of the run.</param>
    /// <returns><see langword="true"/> when the host should speak it.</returns>
    private bool Speaks(AgentResponseUpdate update)
        => _compiled.SpokenBy is not { } spoken
            || (update.AuthorName is { } author && spoken.Contains(author))
            || update.AdditionalProperties?.Contains<TurnDisposition>() is true;

    /// <summary>Runs every writer, then lets the machine pick the stage of the next turn.</summary>
    /// <param name="turn">The turn that just spoke.</param>
    /// <param name="response">What the agent answered.</param>
    /// <param name="toolFault">
    /// The message of the fault that threw out of the run, or <see langword="null"/> when nothing
    /// threw. Section 8.7, row six.
    /// </param>
    /// <param name="cancellationToken">Cancels the extractor call.</param>
    /// <returns>The finished turn.</returns>
    /// <param name="disposition">
    /// What <see cref="ModerationAgent"/> and <see cref="FallbackAgent"/> reported about
    /// this turn, or <see langword="null"/> when neither layer marked it. A flagged turn skips the
    /// extractor: the words moderation flagged are its only input, and a slot filled from them would
    /// carry the flagged content into the state document and into every later prompt. Everything else
    /// about a refused turn is ordinary, so the refusal enters the transcript, the stage machine
    /// advances, and <c>turn.completed</c> proves the refusal under
    /// <see cref="AuditPayloadKeys.ReplyTextSha256"/>.
    /// </param>
    private async Task<TurnResult> CompleteTurnAsync(
        Turn turn,
        AgentResponse response,
        string? toolFault,
        TurnDisposition? disposition,
        CancellationToken cancellationToken)
    {
        var interruption = CurrentInterruption();

        // The moderation facts of this turn, raised before the turn's own events so the flag still
        // takes the lower ordinal. The verdict is known before the model runs, which is the one rule
        // that separates prompt.flagged from reply.interrupted: it amends nothing.
        var refused = disposition?.Moderation is ModerationOutcome.Flagged;
        _events.RaiseModeration(turn.Index, disposition);

        // R1 reaches the turn through the fallback layer, and a fault above every chat client — a
        // graph that matched no edge — still reaches the catch in the run methods. Both are the
        // same row-six fact.
        toolFault ??= disposition is { Fallback: FallbackCause.Faulted, FallbackReason: { } caught }
            ? caught
            : null;
        var reply = SpokenReply.From(response.Messages);
        var spokenReply = reply;
        TimeSpan? interruptedAfter = null;
        string? failure = toolFault is null ? null : ToolFailureReason + " " + toolFault;

        if (interruption is { } cut)
        {
            // Item 6a. The record holds the text the caller heard, not the text the model produced.
            // A trailing space is not speech, so it is trimmed: pipecat pins the same rule in its
            // aggregator test, where the model sent "Hello " and the record holds "Hello".
            reply = cut.HeardText.Trim();
            interruptedAfter = cut.PlayedDuration;
        }
        else if (failure is not null
            || string.IsNullOrWhiteSpace(reply)
            || disposition?.Fallback is FallbackCause.EmptyReply)
        {
            // Section 8.7, last row. A quiet run is silence on a voice call, so an empty reply is a
            // failure even though nothing threw.
            failure ??= EmptyReplyReason;
            reply = _compiled.Configuration.FallbackReply;
            spokenReply = reply;
        }

        // Section 8.7, last row, raised once for the turn. It is diagnostic only, so it takes no
        // ordinal and no row records it; the turn.completed event of this same turn carries the
        // fallback the caller actually heard. The tool fault of row six is raised in
        // WriteTurnEvents instead, because the chain stores that one and its row is stamped with
        // the same endedAt every other event of the turn carries.
        if (toolFault is null && failure is not null)
        {
            _events.RaiseDiagnostic(CallEventKind.EmptyReply, _time.GetUtcNow(), turn.Index);
        }

        // §7's ask is charged only for a turn whose own words reached the caller. The clarification
        // rides an instruction injected before the run, so a run that ended in the fallback reply, or
        // one moderation refused, never put the question — and a slot recorded as asked is silent for
        // the rest of the call.
        if (failure is null && !refused)
        {
            _clarifications.CommitAsks();
        }

        // What this turn adds to the transcript. It is built here and written at the end of the
        // method, in one lock with the rest of what a late barge-in may still amend: the transcript
        // span, the ordinal the amendment must reference, and LastTurn itself. Nothing between here
        // and there reads the transcript — the extractor reads the finished turn, and the writers
        // read the state document — so building it now and committing it once costs nothing.
        // A barge-in inside the first 100 ms leaves no heard text, and an empty assistant message
        // teaches the model nothing. pipecat and livekit both guard this. Both row shapes read this
        // one value, so what counts as words the caller heard is decided once.
        var heard = reply.Length > 0 ? new ChatMessage(ChatRole.Assistant, reply) : null;

        // Rows 3 and 4 record the caller-facing turn alone, so `written` stays empty for them: a
        // node's tool pair and a node's line to the next node are neither said nor heard, and the
        // commit below takes the heard reply straight.
        List<ChatMessage> written = [];
        if (_sessionCarriesHistory)
        {
            if (interruptedAfter is not null || failure is not null)
            {
                // The transcript holds what the caller heard. A run that stopped mid-round would
                // otherwise leave a tool call with no result behind, and the next turn would send
                // it. So the pairs that finished are kept and only an unpaired call is dropped: a
                // side effect that ran must stay visible to the next turn. livekit/agents fixed the
                // same defect in issue 3702.
                written = [.. TurnMessages.FinishedToolMessages(response.Messages)];

                if (heard is not null)
                {
                    written.Add(heard);
                }
            }
            else
            {
                written = [.. response.Messages];
            }
        }

        // The per-turn writers run from here, in the order the constructor documents: tool results
        // first.
        ApplyToolResults(response.Messages);

        // The extractor writes next. Its deadline is here and not on the whole method, because the
        // raise of the turn's events and the stage advance must always run.
        string? extractionFailure = null;

        // A refused turn runs no extractor. The words moderation flagged are the extractor's only
        // input, so a slot filled from them would carry the flagged content into the state document
        // and into every later prompt. The refusal itself says nothing worth extracting either, and
        // the extractor costs a model call, so this also spends nothing on a turn nobody answered.
        if (!refused)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TurnCompletionTimeout);
            try
            {
                extractionFailure = await ExtractAsync(turn, response, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                extractionFailure = ExtractionTimedOutReason;
            }
        }

        if (extractionFailure is not null)
        {
            // Section 8.7, row two: leave the slots unchanged, report once for the turn, and continue
            // the call. State extraction must never drop a call. The reason rides on the event
            // because the line an operator reads is the same one the turn loop used to write itself.
            _events.RaiseDiagnostic(
                CallEventKind.ExtractionFailed,
                _time.GetUtcNow(),
                turn.Index,
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [CallEventPayloadKeys.Reason] = extractionFailure,
                });
        }

        // The clock fields write next. The clock comes from the injected provider, so a test owns
        // it. The same read stamps every audit event of this turn.
        var endedAt = _time.GetUtcNow();
        State.TurnIndex++;
        State.CallDurationSeconds = (endedAt - _startedAt).TotalSeconds;

        // The counters write last.
        _counters.Apply(State);

        var stageAfter = turn.StageBefore;
        if (_policy is not null)
        {
            stageAfter = _policy.Advance(State.Snapshot());
            State.Stage = stageAfter;
            IsComplete = _policy.IsTerminal;
        }

        TurnResult result = new(
            CallId,
            turn.Index,
            turn.StageBefore,
            stageAfter,
            reply,
            IsComplete,
            extractionFailure,
            failure,
            interruptedAfter,
            endedAt);

        // One lock, one moment. A late barge-in reads all four of these together, so a window in
        // which LastTurn already names this turn while the span or the ordinal still names the one
        // before it would let an amendment rewrite the wrong part of the transcript.
        lock (_interruptLock)
        {
            // What the caller said and what the caller heard, written together.
            if (_sessionCarriesHistory)
            {
                LastReplyMessageId = _history.AppendTurn(
                    turn.Session, [turn.Spoken, .. written], Snapshot(), turn.MessageId);
            }
            else
            {
                // Rows 3 and 4 answer with one reply per node, and the caller hears one of them.
                // Store 1 holds the caller-facing turn alone, so the deliberation that produced the
                // answer never enters the record and never reaches a later turn.
                LastReplyMessageId = _history.AppendCallerFacingTurn(
                    turn.Session, turn.Spoken, heard, Snapshot(), turn.MessageId);
            }

            // Only now. The chain stores a hash of the spoken text and store 1 stores the text, so a
            // reply.interrupted raised before the append would name a hash of words nothing holds.
            var completedEventId = _events.WriteTurnEvents(
                turn.Index, endedAt, turn.StageBefore, stageAfter, reply, spokenReply, toolFault, interruptedAfter);

            // A turn one barge-in already cut is not amendable again, so it is not published here.
            _amendableEventId = interruptedAfter is null ? completedEventId : null;
            LastTurn = result;

            // The second read. The first one happened before the extractor await, and that await is
            // bounded only by TurnCompletionTimeout, so a barge-in has five whole seconds in which
            // to land after this turn already decided it was not interrupted. On a streaming turn it
            // finds _runCancellation still set and the run still audible, because EndRun does not
            // run until this method has returned, so it takes the in-flight path, cancels a run that
            // has already stopped, and answers true — and true, on the contract of Interrupt, means
            // the barge-in is recorded. (A turn that never streamed was never audible, so its frame
            // already took the amendment path against the turn before this one and never lands
            // here.) Nothing recorded the streaming frame. Reading _interruption again here, one
            // statement after _amendable and LastTurn were published, hands that frame to the very
            // path a frame arriving one instant later would have taken: the tested amendment, which
            // writes the TurnCompleted then ReplyInterrupted pair T23 asks for. The _amendableEventId
            // guard above already fits: it is published exactly when interruptedAfter is null, which
            // is the only state this window can be reached in.
            if (interruption is null
                && _interruption is { } late
                && AmendLastTurn(late)
                && LastTurn is { } amended)
            {
                // The amendment republished LastTurn, so the turn this method returns and the
                // duration the metric below reads must both come from it and not from the record
                // built before the frame landed.
                result = amended;
                interruptedAfter = amended.InterruptedAfter;
            }

            // Consumed, on whichever path recorded it: the in-flight read at the top of this method
            // fed WriteTurnEvents, and the amendment above wrote its own pair. Nothing downstream
            // may handle one frame twice.
            _interruption = null;

            // The turn is committed, so the run is no longer what the caller is hearing. This is the
            // tail of the same defect the read above fixes: a barge-in landing between this lock and
            // EndRun would otherwise still find the run audible, take the in-flight path, and be
            // recorded nowhere at all. Cleared here, under the lock that just published LastTurn, it
            // reaches AmendLastTurn against this turn instead. EndRun still runs and still owns
            // disposing the source; clearing the field twice costs nothing.
            _runCancellation = null;
            _runIsAudible = false;
        }

        AgentCoreTelemetry.EndTurn(
            turn.Activity,
            _time.GetElapsedTime(turn.StartedAt),
            Outcome(failure, interruptedAfter),
            stageAfter,
            failure);

        if (IsComplete)
        {
            // The machine reached a terminal stage, so the call is over and the chain closes here.
            // The stage rides as detail, because the reason a report counts is the same one for
            // every terminal stage the document declares.
            _events.EndCall(CallEndReason.AgentCompleted, endedAt, stageAfter);
        }

        return result;
    }

    /// <summary>Reads the closed outcome value of one finished turn.</summary>
    /// <param name="failure">The section 8.7 reason, or <see langword="null"/>.</param>
    /// <param name="interruptedAfter">The played duration, or <see langword="null"/>.</param>
    /// <returns>One of the three values the metric attribute takes.</returns>
    private static string Outcome(string? failure, TimeSpan? interruptedAfter) => (failure, interruptedAfter) switch
    {
        (not null, _) => AgentCoreTelemetry.OutcomeFailed,
        (_, not null) => AgentCoreTelemetry.OutcomeInterrupted,
        _ => AgentCoreTelemetry.OutcomeCompleted,
    };

    /// <summary>Reads what the turn layers reported about one finished turn.</summary>
    /// <param name="response">What the agent answered.</param>
    /// <returns>The disposition, or <see langword="null"/> when no layer marked the turn.</returns>
    private static TurnDisposition? ReadDisposition(AgentResponse response)
        => response.AdditionalProperties is { } properties
            && properties.TryGetValue<TurnDisposition>(out var disposition)
            ? disposition
            : null;

    /// <summary>Reads what the turn layers reported across the updates of one streaming turn.</summary>
    /// <param name="updates">Every update the run produced, in order, before the seam filtered them.</param>
    /// <returns>The disposition, or <see langword="null"/> when no update carried one.</returns>
    private static TurnDisposition? ReadDisposition(List<AgentResponseUpdate> updates)
    {
        TurnDisposition? found = null;
        foreach (var update in updates)
        {
            if (update.AdditionalProperties is { } properties
                && properties.TryGetValue<TurnDisposition>(out var disposition))
            {
                found = disposition;
            }
        }

        return found;
    }

    /// <summary>Runs the extractor against the finished turn.</summary>
    /// <param name="turn">The turn that just spoke.</param>
    /// <param name="response">What the agent answered.</param>
    /// <param name="cancellationToken">Cancels the model call.</param>
    /// <returns>The reason the extractor produced nothing, or <see langword="null"/>.</returns>
    private async Task<string?> ExtractAsync(Turn turn, AgentResponse response, CancellationToken cancellationToken)
    {
        if (_extractor is null || _compiled.Configuration.Extractor is not { When: ExtractorTrigger.AfterReply })
        {
            return null;
        }

        // The extractor reads the finished turn and, in front of it, the last thing the agent said
        // before the caller spoke. A caller answering a question — "the ENT one", "yes", "the second
        // one" — cannot be read without the question, and the state document cannot carry it because
        // nothing was written yet. The rest of the call stays out: the document already carries every
        // earlier answer. Measured 2026-09-02: with the question in view every such answer resolved,
        // and a turn where only the agent had named a machine still filled nothing.
        List<ChatMessage> finished = [turn.Spoken, .. response.Messages];
        if (Transcript.LastOrDefault(message => message.Role == ChatRole.Assistant && message.Text.Length > 0) is { } asked)
        {
            finished.Insert(0, asked);
        }

        // A failed extraction never drops the turn. The result carries the reason instead.
        var result = await _extractor.ExtractAsync(State, finished, _clarifications, cancellationToken)
            .ConfigureAwait(false);
        return result.Failure;
    }

    /// <summary>Fills every tool-written slot from the tool results of one turn.</summary>
    /// <param name="messages">The messages the agent produced, oldest first.</param>
    private void ApplyToolResults(IEnumerable<ChatMessage> messages)
    {
        // The name a call carries is the declared tool id, because the compile table names every
        // function after the tools: entry it built.
        ToolCallNames names = new();

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        names.Called(call);
                        break;

                    case FunctionResultContent result when names.Of(result) is { } toolId:
                        ToolStateWriter.Apply(State, toolId, ToolResultJson.ToNode(result.Result));
                        break;

                    default:
                        break;
                }
            }
        }
    }

    /// <summary>Picks the agent that speaks this turn.</summary>
    /// <returns>The agent.</returns>
    private AIAgent ResolveAgent()
    {
        if (_policy is null)
        {
            // Row 1, row 3, and row 4 of the compile table. One entry agent answers every turn.
            return _compiled.TurnAgent;
        }

        if (_policy.CurrentAgentId is not { Length: > 0 }
            || _compiled.TurnAgentForStage(_policy.Stage) is not { } agent)
        {
            throw new InvalidOperationException(
                $"The stage '{_policy.Stage}' of the call '{CallId}' names no agent, so no turn can run.");
        }

        return agent;
    }

    /// <summary>Everything one turn carries from its start to its end.</summary>
    /// <param name="Agent">The agent the stage names.</param>
    /// <param name="Session">The session of the call. Store 1 lives in it.</param>
    /// <param name="Request">The messages the run reads.</param>
    /// <param name="Spoken">What the caller said.</param>
    /// <param name="Reminder">The <c>&lt;system-reminder&gt;</c> this one invocation carries, or null.</param>
    /// <param name="StageBefore">The stage the turn spoke in.</param>
    /// <param name="Index">The zero-based index of the turn.</param>
    /// <param name="Activity">The span of this turn, or <see langword="null"/> when nothing listens.</param>
    /// <param name="StartedAt">The timestamp the duration is measured from.</param>
    /// <param name="Renders">
    /// What this turn draws into, or <see langword="null"/> when it has no screen. Built once per
    /// turn for the same reason <see cref="Activity"/> is: <see cref="EnterAmbients"/> reopens its
    /// scope on every step of a streaming turn, and a fresh collector each time would lose whatever
    /// an earlier step drew before a later step could attach it to a message.
    /// </param>
    /// <param name="Sources">
    /// What this turn cites into. Built once per turn for the same reason <see cref="Renders"/> is,
    /// and never null: a call with no screen still answers from documents.
    /// </param>
    /// <param name="Knowledge">
    /// What this turn may see of the knowledge base. Built once per turn for the same reason
    /// <see cref="Renders"/> is, and additionally because a scope that changed between two updates
    /// of one streaming turn would search two different corpora for one answer.
    /// </param>
    /// <param name="MessageId">
    /// What the caller calls the message it sent, or <see langword="null"/> when it named none. The
    /// append stamps it on the row so a later edit has something to anchor on.
    /// </param>
    private sealed record Turn(
        AIAgent Agent,
        AgentSession Session,
        List<ChatMessage> Request,
        ChatMessage Spoken,
        string? Reminder,
        string StageBefore,
        int Index,
        Activity? Activity,
        long StartedAt,
        TurnRenders? Renders,
        TurnSources Sources,
        KnowledgeScope? Knowledge,
        string? MessageId);

    /// <summary>The session a graph row's call holds, so store 1 has somewhere to live.</summary>
    private sealed class CallHistorySession : AgentSession;

    /// <summary>What the relay reported when the caller spoke over the reply.</summary>
    /// <param name="HeardText">The text the caller actually heard.</param>
    /// <param name="PlayedDuration">How much of the reply played, at 1 ms.</param>
    private sealed record Interruption(string HeardText, TimeSpan PlayedDuration);
}

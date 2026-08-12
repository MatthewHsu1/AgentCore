using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Policy;
using AgentCore.Application.Ports;
using AgentCore.Application.State;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The turn loop of one call. It owns the state, the stage machine, and the transcript.
/// </summary>
/// <remarks>
/// <para>
/// The compiled agent is a process singleton, so nothing per call may live on it. Everything per call
/// lives here: one <see cref="StateDocument"/>, one <see cref="StagePolicy"/>, and one transcript.
/// One session belongs to one call and runs one turn at a time, so it takes no lock over its state.
/// </para>
/// <para>
/// A <c>graph:</c> with a guarded edge reads that same state from inside the shared graph. The turn
/// opens <see cref="CallStateScope"/> over <see cref="State"/> for as long as the run lasts, so the
/// edge predicate finds the state of this call and of no other. Two calls that run at the same time
/// therefore take different edges through one compiled graph.
/// </para>
/// <para>
/// The session owns the transcript rather than an agent-bound session object. A <c>policy:</c>
/// document switches the <c>AIAgent</c> between stages, and a conversation bound to one agent cannot
/// carry a call that changes agent. The turn loop therefore passes the accumulated messages into each
/// run, and every stage reads the whole call.
/// </para>
/// <para>
/// The writers run in one fixed order, and every turn repeats it:
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="ConstStateWriter"/>, once, when the session is created. A constant never changes.
/// </description></item>
/// <item><description>
/// <see cref="ToolStateWriter"/>, for each tool result the turn produced, in the order the agent
/// produced them. A tool ran inside the turn, so its answer is older than the reply.
/// </description></item>
/// <item><description>
/// <see cref="StateExtractor"/>, after the reply completes, because <c>extractor.when:
/// after_reply</c> reads the finished turn. It runs after the tool writer so the model cannot
/// overwrite a fact a tool already reported for the same turn.
/// </description></item>
/// <item><description>
/// The reserved slots: <c>turnIndex</c> and <c>callDurationSeconds</c>. They move before the counters
/// so a counter rule and an exit guard read the same turn. <c>turnIndex</c> counts the finished
/// turns, so it names the turn that runs next, while <see cref="TurnResult.TurnIndex"/> names the
/// turn that just ran.
/// </description></item>
/// <item><description>
/// <see cref="CounterStateWriter"/>, which reads one snapshot of everything above.
/// </description></item>
/// </list>
/// <para>
/// Only then does <see cref="StagePolicy.Advance"/> run. The stage the machine holds while the
/// writers run is still the stage the turn spoke in, which is what a rule such as
/// <c>{ "===": [ { var: stage }, "resolve" ] }</c> means.
/// </para>
/// <para>
/// Every turn ends with a spoken line, and the writers always run. Section 8.7 names two failures
/// that must not reach the host: a run that returns quietly with no text after 40 tool rounds, and a
/// tool that fails four times in a row. The turn loop catches both, speaks
/// <see cref="FallbackReply"/>, reports the reason on <see cref="TurnResult.Failure"/>, logs it
/// once, and leaves the session ready for the next turn.
/// </para>
/// <para>
/// The session is also where the audit chain of D23 is produced, because it is the only place that
/// knows the turn index, the stage before and after, and the sequence a later amendment must
/// reference. It allocates <see cref="AuditEvent.Sequence"/> itself, counting from zero for each
/// call, and it never waits for the sink: section 7 measures a durable insert at 13 ms p50 against 91
/// nanoseconds to enqueue, so the reply leaves before the row exists.
/// </para>
/// <para>
/// One turn is one span and one duration sample. The call id goes on the span and never on a metric,
/// which is the rule of T61. See <see cref="AgentCoreTelemetry"/>.
/// </para>
/// </remarks>
public sealed class CallSession : IConversationPort
{
    /// <summary>The line the caller hears when a turn fails and the document names none.</summary>
    /// <remarks>
    /// Section 8.7 asks for a spoken fallback and names no text. The document names it through the
    /// optional <c>fallbackReply</c> key, and the turn speaks
    /// <see cref="AgentCoreConfiguration.FallbackReply"/>. This constant is the value a document that
    /// omits the key takes, and a host and a test both name it here.
    /// </remarks>
    public const string FallbackReply = AgentCoreConfiguration.DefaultFallbackReply;

    /// <summary>The reason a turn that produced no text reports.</summary>
    /// <remarks>
    /// Section 8.7, last row: <c>MaximumIterationsPerRequest</c> is 40, request 41 goes out with no
    /// tools, and the run returns quietly. On a voice call that silence is the failure, so the turn
    /// loop reads the reply and never trusts the absence of an exception.
    /// </remarks>
    public const string EmptyReplyReason = "the run returned an empty reply, so the turn spoke the fallback.";

    /// <summary>The reason a turn that lost its tool budget reports, before the message of the fault.</summary>
    /// <remarks>
    /// Section 8.7, sixth row: <c>MaximumConsecutiveErrorsPerRequest</c> is 3, so the 4th consecutive
    /// tool failure throws out of the run. The turn loop catches it and the call stays alive.
    /// </remarks>
    public const string ToolFailureReason = "a tool failed four times in a row, so the turn spoke the fallback.";

    private readonly CompiledAgent _compiled;
    private readonly StagePolicy? _policy;
    private readonly StateExtractor? _extractor;
    private readonly CounterStateWriter _counters;
    private readonly TimeProvider _time;
    private readonly IAuditSinkPort _audit;
    private readonly ILogger _logger;
    private readonly DateTimeOffset _startedAt;
    private readonly List<ChatMessage> _transcript = [];
    private readonly Lock _interruptLock = new();
    private CancellationTokenSource? _runCancellation;
    private Interruption? _interruption;
    private long _sequence;
    private int _running;
    private int _ended;

    /// <summary>Creates the session of one call.</summary>
    /// <param name="callId">The id of the call.</param>
    /// <param name="compiled">The compiled agent. It is shared by every call.</param>
    /// <param name="guards">The evaluator that runs each exit guard and each increment rule.</param>
    /// <param name="extractor">The extractor, or <see langword="null"/> when the document declares none.</param>
    /// <param name="timeProvider">The clock the reserved <c>callDurationSeconds</c> slot reads.</param>
    /// <param name="auditSink">
    /// The sink the chain of D23 is appended to, or <see langword="null"/> for a sink that writes
    /// nowhere.
    /// </param>
    /// <param name="logger">
    /// The logger the three "log once" rows of section 8.7 write to, or <see langword="null"/> for
    /// <see cref="NullLogger.Instance"/>.
    /// </param>
    internal CallSession(
        string callId,
        CompiledAgent compiled,
        IGuardEvaluator guards,
        StateExtractor? extractor,
        TimeProvider timeProvider,
        IAuditSinkPort? auditSink = null,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(guards);
        ArgumentNullException.ThrowIfNull(timeProvider);

        CallId = callId;
        _compiled = compiled;
        _extractor = extractor;
        _counters = new CounterStateWriter(guards);
        _time = timeProvider;

        // Both seams are optional and both have a working default. A host that binds neither still
        // answers a call, and the library never throws for want of either.
        _audit = auditSink ?? NullAuditSink.Instance;
        _logger = logger ?? NullLogger.Instance;
        _startedAt = timeProvider.GetUtcNow();

        // A document with no policy: has no stage machine. The single-agent row and both graph rows
        // read that way, and neither of them ever ends a call by itself.
        _policy = compiled.Configuration.Policy is null ? null : compiled.CreatePolicy(guards);
        State = new StateDocument(compiled.Configuration, _policy?.Stage);

        // Writer order, step 1.
        ConstStateWriter.Apply(State);

        // Sequence 0 of the chain. The call started, and no turn has run.
        Append(NewEvent(AuditEventKind.CallStarted, _startedAt, turnIndex: null));
    }

    /// <summary>Gets the id of the call.</summary>
    public string CallId { get; }

    /// <summary>Gets the stage the machine holds. It is empty when the document declares no policy.</summary>
    public string Stage => State.Stage;

    /// <summary>Gets whether the call reached a terminal stage. A document with no policy never does.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Gets the state of this call. Every guard and every increment rule reads it.</summary>
    public StateDocument State { get; }

    /// <summary>Gets the conversation, oldest first. The session owns it, and every stage reads it.</summary>
    public IReadOnlyList<ChatMessage> Transcript => _transcript;

    /// <summary>Gets the turn that finished last, or <see langword="null"/> before the first turn ends.</summary>
    public TurnResult? LastTurn { get; private set; }

    /// <summary>Gets the compiled agent this session runs. Every call shares it.</summary>
    public CompiledAgent Compiled => _compiled;

    /// <summary>Runs one turn end to end, and returns what it did.</summary>
    /// <param name="userInput">What the caller said.</param>
    /// <param name="cancellationToken">Cancels the model calls.</param>
    /// <returns>The finished turn. It always carries a spoken line.</returns>
    /// <remarks>
    /// A tool that fails four times in a row throws out of the run, and section 8.7 says that must
    /// never kill the call. The turn ends with <see cref="FallbackReply"/>, the writers still run in
    /// their fixed order, and the next turn of the same session starts normally.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The call already reached a terminal stage, another turn of this call is still running, or the
    /// stage the machine holds names no agent.
    /// </exception>
    public async Task<TurnResult> RunTurnAsync(string userInput, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userInput);

        var turn = BeginTurn(userInput);
        var cancellation = StartRun(cancellationToken);
        try
        {
            // Row 4 of the compile table. The compiled graph is a process singleton, so a guarded
            // edge inside it reads the state of the call that runs now, through this scope. The
            // using statement closes the scope when the turn ends, when it throws, and when it is
            // cancelled.
            using var scope = CallStateScope.Enter(State);

            AgentResponse response;
            string? toolFault = null;

            try
            {
                response = await turn.Agent
                    .RunAsync(turn.Request, cancellationToken: cancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (CurrentInterruption() is not null)
            {
                // The caller cut the reply off. What the caller heard comes from the relay.
                response = new AgentResponse();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Section 8.7, sixth row. The run throws, the turn ends, and the call lives.
                toolFault = exception.Message;
                response = new AgentResponse();
            }

            LastTurn = await CompleteTurnAsync(turn, response, toolFault, cancellationToken).ConfigureAwait(false);
            return LastTurn;
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
    /// <remarks>
    /// <para>
    /// The turn finishes when the enumeration finishes. After that, <see cref="LastTurn"/> holds the
    /// finished turn, the writers have run, and the machine holds the stage of the next turn. A
    /// caller that stops enumerating early stops the turn, and the state does not move. A caller that
    /// interrupts calls <see cref="Interrupt"/> instead, which ends the turn and moves the state.
    /// </para>
    /// <para>
    /// The stream is filtered. Section 8.6 measured <c>AsAIAgent()</c>: it yields 47 updates for 40
    /// text fragments, and seven of them carry no content because they are lifecycle events. Every
    /// host would otherwise write the same filter, and a host that forgot it would drift its
    /// character cursor. This seam therefore drops an update that carries nothing and an update whose
    /// only content is empty text. A tool call and a tool result still reach the host, because they
    /// are content a host may show.
    /// </para>
    /// <para>
    /// A tool that fails four times in a row ends the enumeration early. The turn then ends with
    /// <see cref="FallbackReply"/>, exactly as <see cref="RunTurnAsync"/> does.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The call already reached a terminal stage, another turn of this call is still running, or the
    /// stage the machine holds names no agent.
    /// </exception>
    public async IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingAsync(
        string userInput,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userInput);

        var turn = BeginTurn(userInput);
        var cancellation = StartRun(cancellationToken);
        try
        {
            List<AgentResponseUpdate> updates = [];
            string? toolFault = null;

            // Row 4 of the compile table. Control crosses into the compiled graph twice: here, where
            // the wrapper builds the stream, and again inside each round below. This scope covers the
            // first crossing and everything up to the first yield.
            using var opening = CallStateScope.Enter(State);

            var stream = turn.Agent
                .RunStreamingAsync(turn.Request, cancellationToken: cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);

            try
            {
                while (true)
                {
                    AgentResponseUpdate update;

                    // The enumeration itself sits in its own try, because a method that yields takes
                    // no catch clause around the yield.
                    try
                    {
                        // The second crossing, once for each round. An async iterator restores the
                        // execution context of its caller at every yield, so the scope above reaches
                        // the first round only. The graph runs inside MoveNextAsync, so the scope
                        // opens again here and closes with the round.
                        using var round = CallStateScope.Enter(State);

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
                    if (CarriesContent(content))
                    {
                        // The host speaks this, so it leaves the seam as fast as the model produced it.
                        yield return content;
                    }
                }
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            LastTurn = await CompleteTurnAsync(turn, updates.ToAgentResponse(), toolFault, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            EndRun(cancellation);
            turn.Activity?.Dispose();
        }
    }

    /// <summary>Ends the running turn where the caller cut the reply off.</summary>
    /// <param name="utteranceUntilInterrupt">The text the caller actually heard.</param>
    /// <param name="durationUntilInterrupt">How much of the reply played, as the relay reported it.</param>
    /// <returns>
    /// <see langword="true"/> when a turn was running and now ends, and <see langword="false"/> when
    /// no turn was running.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the barge-in entry point of item 6a. Section 7.1 says the relay reports both values on
    /// its <c>interrupt</c> frame, so both arrive here together and this method measures neither.
    /// Nothing behind this call estimates the duration, which is what item 6c asks for.
    /// </para>
    /// <para>
    /// The running turn stops, and it ends the way a finished turn ends. The transcript keeps
    /// <paramref name="utteranceUntilInterrupt"/> rather than the reply the model produced, the
    /// writers run in their fixed order, and <see cref="TurnResult.ReplyText"/> holds the same heard
    /// text while <see cref="TurnResult.InterruptedAfter"/> holds the played duration.
    /// </para>
    /// <para>
    /// A frame that arrives after the turn already ended answers <see langword="false"/> and changes
    /// nothing. A late frame must not drop a call, which is the same rule section 7.1 gives an
    /// unknown frame.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="durationUntilInterrupt"/> is negative.
    /// </exception>
    public bool Interrupt(string utteranceUntilInterrupt, TimeSpan durationUntilInterrupt)
    {
        ArgumentNullException.ThrowIfNull(utteranceUntilInterrupt);
        ArgumentOutOfRangeException.ThrowIfLessThan(durationUntilInterrupt, TimeSpan.Zero);

        lock (_interruptLock)
        {
            if (_runCancellation is not { } cancellation)
            {
                return false;
            }

            _interruption = new Interruption(utteranceUntilInterrupt, durationUntilInterrupt);
            cancellation.Cancel();
            return true;
        }
    }

    /// <summary>Closes the call, and writes the last event of its chain.</summary>
    /// <param name="reason">Why the call ended, as one member of the closed set.</param>
    /// <returns>
    /// <see langword="true"/> when this call wrote the event, and <see langword="false"/> when the
    /// call had already ended.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The stage machine reaches a terminal stage, and the turn loop closes the chain itself with
    /// <see cref="CallEndReason.AgentCompleted"/>. Every other ending arrives here from the vendor
    /// adapter, because only the adapter sees a hang-up, a conference transfer, or a dropped socket.
    /// A second call answers <see langword="false"/> and changes nothing, which is the same rule
    /// section 7.1 gives a late frame.
    /// </para>
    /// <para>
    /// The reason is closed rather than free text, because §9 makes the chain the only long-term
    /// record and a report counts these endings years later. A caller that holds more detail, such
    /// as the vendor hang-up cause, puts it in another payload key.
    /// </para>
    /// <para>
    /// The session runs no further turn afterwards. The event goes to the sink and nothing waits for
    /// it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="reason"/> is not a member of the closed set.
    /// </exception>
    public bool EndCall(CallEndReason reason)
    {
        // The event is written before the flag moves. A value outside the closed set therefore ends
        // no call and writes nothing.
        var wrote = EndCall(reason, _time.GetUtcNow());
        IsComplete = true;
        return wrote;
    }

    /// <summary>Picks the agent, builds the model input, and takes the turn.</summary>
    /// <param name="userInput">What the caller said.</param>
    /// <returns>Everything the rest of the turn needs.</returns>
    private Turn BeginTurn(string userInput)
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

        // The reminder rides a request that happens anyway, and it rides exactly one. The transcript
        // keeps what the caller said, so a stale reminder never repeats in a later turn.
        var reminder = _policy is null ? null : UnfilledSlotReminder.Build(State, _policy.CurrentStage);
        ChatMessage spoken = new(ChatRole.User, userInput);
        List<ChatMessage> request =
            [.. _transcript, new ChatMessage(ChatRole.User, UnfilledSlotReminder.Prepend(reminder, userInput))];

        _transcript.Add(spoken);

        // One turn is one span. The call id rides here, on a span attribute, because T61 refuses it
        // on a metric. The span is disposed in the finally of whichever run method opened the turn.
        var activity = AgentCoreTelemetry.StartTurn(CallId, State.TurnIndex, State.Stage);
        return new Turn(agent, request, spoken, State.Stage, State.TurnIndex, activity, _time.GetTimestamp());
    }

    /// <summary>Opens the window in which <see cref="Interrupt"/> reaches this turn.</summary>
    /// <param name="cancellationToken">The token of the host.</param>
    /// <returns>The source the run reads. The caller ends it with <see cref="EndRun"/>.</returns>
    private CancellationTokenSource StartRun(CancellationToken cancellationToken)
    {
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (_interruptLock)
        {
            _interruption = null;
            _runCancellation = cancellation;
        }

        return cancellation;
    }

    /// <summary>Closes the window, and frees the session for the next turn.</summary>
    /// <param name="cancellation">The source <see cref="StartRun"/> returned.</param>
    private void EndRun(CancellationTokenSource cancellation)
    {
        // The field drops first. A frame that arrives now answers false rather than meeting a
        // disposed source.
        lock (_interruptLock)
        {
            _runCancellation = null;
        }

        cancellation.Dispose();
        Volatile.Write(ref _running, 0);
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

    /// <summary>Reads whether one update carries something a host needs.</summary>
    /// <param name="update">One update of the run.</param>
    /// <returns>Whether the host reads it.</returns>
    /// <remarks>
    /// Section 8.6: seven of the 47 updates of one 40-fragment reply are lifecycle events and carry
    /// no content. This drops those, and it drops an update whose only content is empty text. Every
    /// other content passes, so a tool call still reaches a host that shows one.
    /// </remarks>
    private static bool CarriesContent(ChatResponseUpdate update)
        => update.Contents.Any(content => content is not TextContent text || text.Text.Length > 0);

    /// <summary>Runs every writer, then lets the machine pick the stage of the next turn.</summary>
    /// <param name="turn">The turn that just spoke.</param>
    /// <param name="response">What the agent answered.</param>
    /// <param name="toolFault">
    /// The message of the fault that threw out of the run, or <see langword="null"/> when nothing
    /// threw. Section 8.7, row six.
    /// </param>
    /// <param name="cancellationToken">Cancels the extractor call.</param>
    /// <returns>The finished turn.</returns>
    /// <remarks>
    /// This is where the chain of D23 is written, because this is where the turn index, both stages,
    /// and the moment the turn ended are all known at once. The clock is read exactly once, so every
    /// event of the turn carries the same instant.
    /// </remarks>
    private async Task<TurnResult> CompleteTurnAsync(
        Turn turn,
        AgentResponse response,
        string? toolFault,
        CancellationToken cancellationToken)
    {
        var interruption = CurrentInterruption();
        var reply = response.Text;
        var spokenReply = reply;
        TimeSpan? interruptedAfter = null;
        string? failure = toolFault is null ? null : ToolFailureReason + " " + toolFault;

        if (interruption is { } cut)
        {
            // Item 6a. The record holds the text the caller heard, not the text the model produced.
            reply = cut.HeardText;
            interruptedAfter = cut.PlayedDuration;
        }
        else if (failure is not null || string.IsNullOrWhiteSpace(reply))
        {
            // Section 8.7, last row. A quiet run is silence on a voice call, so an empty reply is a
            // failure even though nothing threw.
            failure ??= EmptyReplyReason;
            reply = _compiled.Configuration.FallbackReply;
            spokenReply = reply;
        }

        // Section 8.7 says "log once" for each of these rows, and each one runs once for the turn.
        if (toolFault is not null)
        {
            Log.ToolBudgetSpent(_logger, CallId, turn.Index, toolFault);
            AgentCoreTelemetry.RecordFailure(AgentCoreTelemetry.FailureTool);
        }
        else if (failure is not null)
        {
            Log.EmptyReply(_logger, CallId, turn.Index);
            AgentCoreTelemetry.RecordFailure(AgentCoreTelemetry.FailureEmptyReply);
        }

        if (interruptedAfter is not null || failure is not null)
        {
            // The transcript holds what the caller heard. A run that stopped mid-round would
            // otherwise leave a tool call with no result behind, and the next turn would send it.
            _transcript.Add(new ChatMessage(ChatRole.Assistant, reply));
        }
        else
        {
            _transcript.AddRange(response.Messages);
        }

        // Writer order, step 2.
        ApplyToolResults(response.Messages);

        // Writer order, step 3.
        var extractionFailure = await ExtractAsync(turn, response, cancellationToken).ConfigureAwait(false);

        if (extractionFailure is not null)
        {
            // Section 8.7, row two: leave the slots unchanged, log once for the turn, and continue
            // the call. State extraction must never drop a call.
            Log.ExtractionFailed(_logger, CallId, turn.Index, extractionFailure);
            AgentCoreTelemetry.RecordFailure(AgentCoreTelemetry.FailureExtraction);
        }

        // Writer order, step 4. The clock comes from the injected provider, so a test owns it. The
        // same read stamps every audit event of this turn.
        var endedAt = _time.GetUtcNow();
        State.TurnIndex++;
        State.CallDurationSeconds = (endedAt - _startedAt).TotalSeconds;

        // Writer order, step 5.
        _counters.Apply(State);

        var stageAfter = turn.StageBefore;
        if (_policy is not null)
        {
            stageAfter = _policy.Advance(State.Snapshot());
            State.Stage = stageAfter;
            IsComplete = _policy.IsTerminal;
        }

        WriteTurnEvents(turn, endedAt, stageAfter, reply, spokenReply, toolFault, interruptedAfter);

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
            EndCall(CallEndReason.AgentCompleted, endedAt, stageAfter);
        }

        return new TurnResult(
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

    /// <summary>Writes the audit events of one finished turn, in the order they happened.</summary>
    /// <param name="turn">The turn that just spoke.</param>
    /// <param name="endedAt">The moment the turn ended.</param>
    /// <param name="stageAfter">The stage the machine holds after the turn.</param>
    /// <param name="reply">The text the caller heard.</param>
    /// <param name="spokenReply">The whole reply the model produced.</param>
    /// <param name="toolFault">The message of the fault, or <see langword="null"/>.</param>
    /// <param name="interruptedAfter">The played duration, or <see langword="null"/>.</param>
    /// <remarks>
    /// A barge-in writes two events and not one. T23: the chain is append-only, so an amendment is a
    /// second event that references the first, and <c>reply.interrupted</c> names the sequence of the
    /// <c>turn.completed</c> event it corrects. It carries the text the caller ACTUALLY HEARD, which
    /// the relay reported and nothing here estimated. See item 6a.
    /// </remarks>
    private void WriteTurnEvents(
        Turn turn,
        DateTimeOffset endedAt,
        string stageAfter,
        string reply,
        string spokenReply,
        string? toolFault,
        TimeSpan? interruptedAfter)
    {
        if (toolFault is not null)
        {
            // The fault threw out of the run, so the tool that spent the budget is not named here.
            // A missing fact is an absent key, so toolName stays out rather than reading "unknown".
            Append(NewEvent(
                AuditEventKind.ToolFailed,
                endedAt,
                turn.Index,
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.ToolError] = toolFault,
                }));
        }

        var completed = NewEvent(
            AuditEventKind.TurnCompleted,
            endedAt,
            turn.Index,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.ReplyText] = spokenReply,
                [AuditPayloadKeys.StageBefore] = turn.StageBefore,
                [AuditPayloadKeys.StageAfter] = stageAfter,
            });

        Append(completed);

        if (interruptedAfter is not { } played)
        {
            return;
        }

        Append(NewEvent(
            AuditEventKind.ReplyInterrupted,
            endedAt,
            turn.Index,
            amends: completed.Sequence,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.UtteranceUntilInterrupt] = reply,
                [AuditPayloadKeys.DurationUntilInterruptMs] =
                    ((long)played.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            }));
    }

    /// <summary>Builds one event of this call, and takes the next sequence.</summary>
    /// <param name="kind">What the event records.</param>
    /// <param name="occurredAt">When it happened.</param>
    /// <param name="turnIndex">The turn it belongs to, or <see langword="null"/> for a call event.</param>
    /// <param name="amends">The sequence this event corrects, or <see langword="null"/>.</param>
    /// <param name="payload">The facts the event carries.</param>
    /// <returns>The event, ready to append.</returns>
    /// <remarks>
    /// The caller allocates the sequence and not the sink, because the sink answers long after the
    /// turn moved on. The counter is monotonic within one call and starts at zero.
    /// </remarks>
    private AuditEvent NewEvent(
        AuditEventKind kind,
        DateTimeOffset occurredAt,
        int? turnIndex,
        long? amends = null,
        IReadOnlyDictionary<string, string>? payload = null)
        => new()
        {
            CallId = CallId,
            Sequence = Interlocked.Increment(ref _sequence) - 1,
            Kind = kind,
            OccurredAt = occurredAt,
            TurnIndex = turnIndex,
            AmendsSequence = amends,
            Payload = payload ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };

    /// <summary>Hands one event to the sink, and never waits for it.</summary>
    /// <param name="auditEvent">The event to append.</param>
    /// <remarks>
    /// <para>
    /// Section 7 measures a durable insert at 13 ms p50 and 32 ms p99, against 91 nanoseconds to
    /// enqueue, so <b>the sink must never sit on the turn</b>. A sink that completes synchronously
    /// costs the enqueue and nothing else. A sink that does not is observed on a separate task, so
    /// the reply leaves while the row is still being written.
    /// </para>
    /// <para>
    /// Nothing here propagates. Audit is a record of the call and never a part of it, so a sink that
    /// throws is logged once and the turn goes on.
    /// </para>
    /// </remarks>
    private void Append(AuditEvent auditEvent)
    {
        AgentCoreTelemetry.RecordAuditEvent(AuditEventKinds.ToToken(auditEvent.Kind));

        try
        {
            // CancellationToken.None: the enqueue belongs to the record of the call, not to the turn
            // the caller may have cancelled.
            ValueTask pending = _audit.AppendAsync(auditEvent, CancellationToken.None);
            if (pending.IsCompletedSuccessfully)
            {
                return;
            }

            _ = ObserveAppendAsync(pending, auditEvent.Kind);
        }
#pragma warning disable CA1031 // Audit is a record of the call and never a part of it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Log.AuditAppendFailed(_logger, CallId, AuditEventKinds.ToToken(auditEvent.Kind), exception);
        }
    }

    /// <summary>Watches an append that did not finish at once, so no fault goes unobserved.</summary>
    /// <param name="pending">What the sink returned.</param>
    /// <param name="kind">What the event records.</param>
    /// <returns>A task that always completes, and never faults.</returns>
    private async Task ObserveAppendAsync(ValueTask pending, AuditEventKind kind)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The turn already ended. Reporting is all that is left to do.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Log.AuditAppendFailed(_logger, CallId, AuditEventKinds.ToToken(kind), exception);
        }
    }

    /// <summary>Closes the chain of this call, once.</summary>
    /// <param name="reason">Why the call ended.</param>
    /// <param name="endedAt">The moment it ended.</param>
    /// <param name="terminalStage">The stage the machine stopped in, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when this call wrote the event, and <see langword="false"/> when it already had.</returns>
    /// <remarks>
    /// The payload carries the wire token of the reason and never its .NET name, for the reason
    /// <see cref="CallEndReasons"/> gives. The terminal stage rides beside it rather than inside it,
    /// because a stage name is detail and the reason is what a report counts.
    /// </remarks>
    private bool EndCall(CallEndReason reason, DateTimeOffset endedAt, string? terminalStage = null)
    {
        // The token is read first, so a value outside the closed set writes no event at all.
        var token = CallEndReasons.ToToken(reason);

        if (Interlocked.Exchange(ref _ended, 1) == 1)
        {
            return false;
        }

        Dictionary<string, string> payload = new(StringComparer.Ordinal)
        {
            [AuditPayloadKeys.EndReason] = token,
        };

        if (terminalStage is { Length: > 0 })
        {
            payload[AuditPayloadKeys.StageAfter] = terminalStage;
        }

        Append(NewEvent(AuditEventKind.CallEnded, endedAt, turnIndex: null, payload: payload));

        return true;
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

        // The extractor reads one finished turn and not the whole call, which is what its own prompt
        // asks for. The state document already carries every earlier answer, so nothing is lost.
        // The caller's message goes in without the reminder, because a reminder is not a fact.
        List<ChatMessage> finished = [turn.Spoken, .. response.Messages];

        // A failed extraction never drops the turn. The result carries the reason instead.
        var result = await _extractor.ExtractAsync(State, finished, cancellationToken).ConfigureAwait(false);
        return result.Failure;
    }

    /// <summary>Fills every tool-written slot from the tool results of one turn.</summary>
    /// <param name="messages">The messages the agent produced, oldest first.</param>
    private void ApplyToolResults(IEnumerable<ChatMessage> messages)
    {
        // The result carries the call id, and the call carries the name. The name is the declared
        // tool id, because the compile table names every function after the tools: entry it built.
        Dictionary<string, string> toolIdByCall = new(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call)
                {
                    toolIdByCall[call.CallId] = call.Name;
                    continue;
                }

                if (content is FunctionResultContent result
                    && toolIdByCall.TryGetValue(result.CallId, out var toolId))
                {
                    ToolStateWriter.Apply(State, toolId, ToNode(result.Result));
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
            return _compiled.Agent;
        }

        if (_policy.CurrentAgentId is not { Length: > 0 } || _compiled.ForStage(_policy.Stage) is not { } agent)
        {
            throw new InvalidOperationException(
                $"The stage '{_policy.Stage}' of the call '{CallId}' names no agent, so no turn can run.");
        }

        return agent;
    }

    /// <summary>Carries one tool result into the node tree the tool writer reads.</summary>
    /// <param name="value">Whatever the tool returned.</param>
    /// <returns>The node tree, or <see langword="null"/> when the tool returned nothing.</returns>
    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),

        // A tool result has no declared shape. A tool that answers with a JSON document as one
        // string still reaches its slot, and a tool that answers with prose reads as that prose.
        JsonElement element when element.ValueKind is JsonValueKind.String
            => ParseOrText(element.GetString() ?? string.Empty),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        string text => ParseOrText(text),
        bool flag => JsonValue.Create(flag),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>Reads one string as JSON, and falls back to the string itself.</summary>
    /// <param name="text">The text the tool returned.</param>
    /// <returns>The node tree.</returns>
    private static JsonNode? ParseOrText(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // Section 8.7: a tool result has no declared shape, and a tool never drops a turn.
            return JsonValue.Create(text);
        }
    }

    /// <summary>Everything one turn carries from its start to its end.</summary>
    /// <param name="Agent">The agent the stage names.</param>
    /// <param name="Request">The messages the run reads, with the reminder on the last one.</param>
    /// <param name="Spoken">What the caller said, without the reminder.</param>
    /// <param name="StageBefore">The stage the turn spoke in.</param>
    /// <param name="Index">The zero-based index of the turn.</param>
    /// <param name="Activity">The span of this turn, or <see langword="null"/> when nothing listens.</param>
    /// <param name="StartedAt">The timestamp the duration is measured from.</param>
    /// <remarks>
    /// The span travels on this record rather than through <see cref="Activity.Current"/>. An async
    /// iterator restores the execution context of its caller at every yield, so the streaming turn
    /// would otherwise lose the span it opened at the first update it hands the host.
    /// </remarks>
    private sealed record Turn(
        AIAgent Agent,
        List<ChatMessage> Request,
        ChatMessage Spoken,
        string StageBefore,
        int Index,
        Activity? Activity,
        long StartedAt);

    /// <summary>What the relay reported when the caller spoke over the reply.</summary>
    /// <param name="HeardText">The text the caller actually heard.</param>
    /// <param name="PlayedDuration">How much of the reply played, at 1 ms.</param>
    /// <remarks>
    /// Both values arrive on the <c>interrupt</c> frame of section 7.1, and the vendor adapter that
    /// reads that frame owns its schema. This record carries the two values across the seam, so the
    /// core never learns the frame.
    /// </remarks>
    private sealed record Interruption(string HeardText, TimeSpan PlayedDuration);
}

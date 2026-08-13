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
using AgentCore.Application.Evaluation;
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

    /// <summary>The failure the turn records when the completion work passes its deadline.</summary>
    /// <remarks>
    /// This is a safety net against a hung extractor, not a value a document or a host reads by
    /// name, so it stays internal. <c>InternalsVisibleTo</c> already gives the test project the
    /// reach it needs, and D15 makes a public member on <see cref="CallSession"/> a permanent
    /// promise, which this string is not meant to be.
    /// </remarks>
    internal const string ExtractionTimedOutReason = "the turn completion passed its deadline.";

    /// <summary>What the log records when the moderation endpoint runs out of time.</summary>
    /// <remarks>It stays internal for the reason <see cref="ExtractionTimedOutReason"/> gives.</remarks>
    internal const string ModerationTimedOutReason = "the moderation endpoint passed its deadline.";

    /// <summary>What the log records when the moderation endpoint throws.</summary>
    /// <remarks>It stays internal for the reason <see cref="ExtractionTimedOutReason"/> gives.</remarks>
    internal const string ModerationFaultedReason = "the moderation endpoint threw.";

    /// <summary>How long the work after the reply may take before it is abandoned.</summary>
    /// <remarks>
    /// <para>
    /// Section 8.7 keeps a call alive through a failure, and the work after the reply runs on the
    /// host token so a barge-in never cancels it. That is deliberate, and it means nothing else
    /// bounds it. This deadline does.
    /// </para>
    /// <para>
    /// It stays private rather than a document setting. It guards against a hung model call, and it
    /// is not a product knob a document should tune, so it carries no JSON Schema field and no
    /// configuration key. D15 makes a public member here a permanent promise; a private one can
    /// still be promoted later, and a public one could not be taken back.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan TurnCompletionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a turn waits for the moderation endpoint before it answers anyway.</summary>
    /// <remarks>
    /// <para>
    /// <b>This one bound sits in front of the caller, so it is far tighter than
    /// <see cref="TurnCompletionTimeout"/>.</b> Moderation reads what the caller said before the
    /// model runs, so the caller hears silence for as long as it takes. Two seconds is already long
    /// on a voice call. The endpoint answers in a fraction of that, and a wait this long means it is
    /// not answering at all.
    /// </para>
    /// <para>
    /// The design permits this wait where it would refuse a judge. Section 9 says "moderation is not
    /// a judge, it is one free HTTP POST", which exempts it from the D9 rule that keeps a judge off
    /// the turn.
    /// </para>
    /// <para>
    /// It is private, and not a document setting, for the reason <see cref="TurnCompletionTimeout"/>
    /// gives: D15 makes a public member here a permanent promise.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan ModerationTimeout = TimeSpan.FromSeconds(2);

    private readonly CompiledAgent _compiled;
    private readonly StagePolicy? _policy;
    private readonly StateExtractor? _extractor;
    private readonly PromptModerator? _moderation;
    private readonly CounterStateWriter _counters;
    private readonly TimeProvider _time;
    private readonly IAuditSinkPort _audit;
    private readonly ILogger _logger;
    private readonly DateTimeOffset _startedAt;
    private readonly List<ChatMessage> _transcript = [];
    private readonly Lock _interruptLock = new();
    private CancellationTokenSource? _runCancellation;
    private Interruption? _interruption;
    private AmendableTurn? _amendable;
    private long _sequence;
    private int _running;
    private int _ended;

    // Whether the running turn has already handed the host something to speak. A streaming turn
    // that has yielded nothing cannot be the turn the caller was hearing, so a barge-in in that
    // window belongs to the turn that finished before it. See the remarks on <see cref="Interrupt"/>.
    // It is raised by the run, and dropped twice: once in CompleteTurnAsync's commit lock, the
    // moment the turn's own record exists for a late barge-in to amend, and again in EndRun, which
    // is the only clear a turn that never reached that commit gets.
    private volatile bool _runIsAudible;

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
    /// <param name="moderation">
    /// The moderator that reads what the caller said before the model runs, or
    /// <see langword="null"/> for a session that moderates nothing.
    /// </param>
    internal CallSession(
        string callId,
        CompiledAgent compiled,
        IGuardEvaluator guards,
        StateExtractor? extractor,
        TimeProvider timeProvider,
        IAuditSinkPort? auditSink = null,
        ILogger? logger = null,
        PromptModerator? moderation = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(guards);
        ArgumentNullException.ThrowIfNull(timeProvider);

        CallId = callId;
        _compiled = compiled;
        _extractor = extractor;
        _moderation = moderation;
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
    /// <remarks>
    /// A copy, and never the list itself. Three writers reach that list — <c>BeginTurn</c>, the
    /// commit of <c>CompleteTurnAsync</c>, and <c>AmendLastTurn</c> — and a held prompt puts two of
    /// them on two threads at once. A live view handed out here would let a reader enumerate the
    /// list while an amendment splices it, which throws rather than returning a torn conversation.
    /// The copy is taken under the same lock those three writers hold, so what comes back is one
    /// whole conversation as it stood at one instant.
    /// </remarks>
    public IReadOnlyList<ChatMessage> Transcript
    {
        get
        {
            lock (_interruptLock)
            {
                return _transcript.ToArray();
            }
        }
    }

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

        // A run that never streams hands the whole reply over at once, so there is no window in
        // which it has produced nothing the caller could have heard. An interrupt during it
        // therefore always belongs to it.
        var cancellation = StartRun(audibleFromTheStart: true, cancellationToken);
        try
        {
            // Row 4 of the compile table. The compiled graph is a process singleton, so a guarded
            // edge inside it reads the state of the call that runs now, through this scope. The
            // using statement closes the scope when the turn ends, when it throws, and when it is
            // cancelled.
            using var scope = CallStateScope.Enter(State);

            // Moderation reads what the caller said before the model runs. A flagged turn never
            // reaches the model, so nothing of it is generated and nothing of it is extracted.
            if (await RefuseFlaggedPromptAsync(turn, userInput, cancellation.Token).ConfigureAwait(false)
                is { } refusal)
            {
                return await CompleteTurnAsync(turn, refusal, toolFault: null, cancellationToken, refused: true)
                    .ConfigureAwait(false);
            }

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

            // CompleteTurnAsync publishes LastTurn itself, under the same lock that records what a
            // late barge-in may still amend. Assigning it a second time here could overwrite an
            // amendment that landed in between.
            return await CompleteTurnAsync(turn, response, toolFault, cancellationToken).ConfigureAwait(false);
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

        // A streaming turn becomes audible only once it hands the host its first piece of content.
        // Until then nothing of it has reached the caller, and a barge-in belongs elsewhere.
        var cancellation = StartRun(audibleFromTheStart: false, cancellationToken);
        try
        {
            List<AgentResponseUpdate> updates = [];
            string? toolFault = null;

            // Row 4 of the compile table. Control crosses into the compiled graph twice: here, where
            // the wrapper builds the stream, and again inside each round below. This scope covers the
            // first crossing and everything up to the first yield.
            using var opening = CallStateScope.Enter(State);

            // Moderation reads what the caller said before the model runs, so a flagged turn opens no
            // stream at all. The host receives the refusal as one update, the same shape it would
            // receive a one-chunk reply in, so nothing downstream learns that this turn was refused.
            if (await RefuseFlaggedPromptAsync(turn, userInput, cancellation.Token).ConfigureAwait(false)
                is { } refusal)
            {
                _runIsAudible = true;
                yield return new ChatResponseUpdate(ChatRole.Assistant, refusal.Text);

                _ = await CompleteTurnAsync(turn, refusal, toolFault: null, cancellationToken, refused: true)
                    .ConfigureAwait(false);
                yield break;
            }

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

            // CompleteTurnAsync publishes LastTurn itself. See the same note in RunTurnAsync.
            _ = await CompleteTurnAsync(turn, updates.ToAgentResponse(), toolFault, cancellationToken)
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
    /// <remarks>
    /// <para>
    /// This is the barge-in entry point of item 6a. Section 7.1 says the relay reports both values on
    /// its <c>interrupt</c> frame, so both arrive here together and this method measures neither.
    /// Nothing behind this call estimates the duration, which is what item 6c asks for.
    /// </para>
    /// <para>
    /// The frame reaches one of two paths, and which one depends on whether the running turn is
    /// the turn the caller was hearing. This session answers that from its own side — a run that has
    /// handed the host nothing cannot be the turn anyone heard — and that answer is right whenever
    /// the caller of this method is also the one speaking the reply. A vendor that paces the audio
    /// itself knows better: it alone can tell that the turn it is still speaking is not the turn now
    /// running, because a held prompt already started the next one. <paramref name="cutsRunningTurn"/>
    /// is how it says so. That is a fact about the call and not a frame schema, so D8 still holds:
    /// nothing of section 7.1's wire format crosses this seam.
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>The running turn is audible, and it is the turn the caller was hearing.</b> It stops, and
    /// it ends the way a finished turn ends. The
    /// transcript keeps <paramref name="utteranceUntilInterrupt"/> rather than the reply the model
    /// produced, the writers run in their fixed order, and <see cref="TurnResult.ReplyText"/> holds
    /// the same heard text while <see cref="TurnResult.InterruptedAfter"/> holds the played duration.
    /// </description></item>
    /// <item><description>
    /// <b>No turn is running, the one that is has produced nothing yet, or
    /// <paramref name="cutsRunningTurn"/> is <see langword="false"/>.</b> The vendor paces the
    /// audio, so a reply is still playing long after the model finished streaming it, and a
    /// held prompt can already have started the next turn inside the finished turn's own ending. The
    /// caller was therefore hearing the turn that finished last, and that turn is amended:
    /// <see cref="LastTurn"/> takes the heard text and the played duration, the transcript span of
    /// that turn is rewritten to hold what the caller heard, and the chain takes a second event. A
    /// turn that has produced nothing is never cut short, because nobody has heard it.
    /// </description></item>
    /// </list>
    /// <para>
    /// The amendment is an event and never an edit. T23: the chain is append-only, so
    /// <see cref="AuditEventKind.ReplyInterrupted"/> names the sequence of the
    /// <see cref="AuditEventKind.TurnCompleted"/> event it corrects through
    /// <see cref="AuditEvent.AmendsSequence"/>.
    /// </para>
    /// <para>
    /// A frame that arrives when no turn has ever run answers <see langword="false"/> and changes
    /// nothing, and so does a second frame against a turn one barge-in already cut. A late frame
    /// must not drop a call, which is the same rule section 7.1 gives an unknown frame.
    /// </para>
    /// </remarks>
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
            // decide, exactly as before.
            if (cutsRunningTurn && _runCancellation is { } cancellation && _runIsAudible)
            {
                _interruption = new Interruption(utteranceUntilInterrupt, durationUntilInterrupt);
                cancellation.Cancel();
                return true;
            }

            return AmendLastTurn(utteranceUntilInterrupt, durationUntilInterrupt);
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
        // keeps what the caller said, so a stale reminder never repeats in a later turn. It reads
        // the state document and never the transcript, so it is built before the lock below.
        var reminder = _policy is null ? null : UnfilledSlotReminder.Build(State, _policy.CurrentStage);
        ChatMessage spoken = new(ChatRole.User, userInput);

        // The lock guards <see cref="_transcript"/>, and this is the only place a turn's own start
        // touches it. One session runs one turn at a time, but a held prompt puts this method and
        // <see cref="AmendLastTurn"/> on two threads at once: the relay starts the next turn from
        // inside the finished turn's own finally, on that turn's thread, while the read loop can be
        // inside the amendment rewriting the finished turn's span from the other. AmendLastTurn is
        // that other writer, and CompleteTurnAsync's own commit is the third; both already hold
        // this same lock. List<T> is not safe for two writers, so the read that builds the request
        // and the append of the spoken message are one step under it — a request built from a list
        // an amendment is halfway through splicing would otherwise hold a torn conversation.
        List<ChatMessage> request;
        lock (_interruptLock)
        {
            request =
                [.. _transcript, new ChatMessage(ChatRole.User, UnfilledSlotReminder.Prepend(reminder, userInput))];

            _transcript.Add(spoken);
        }

        // One turn is one span. The call id rides here, on a span attribute, because T61 refuses it
        // on a metric. The span is disposed in the finally of whichever run method opened the turn.
        var activity = AgentCoreTelemetry.StartTurn(CallId, State.TurnIndex, State.Stage);
        return new Turn(agent, request, spoken, State.Stage, State.TurnIndex, activity, _time.GetTimestamp());
    }

    /// <summary>Opens the window in which <see cref="Interrupt"/> reaches this turn.</summary>
    /// <param name="audibleFromTheStart">
    /// Whether a barge-in belongs to this run from its first instant. A run that never streams
    /// answers <see langword="true"/>, because it hands the whole reply over at once. A streaming
    /// run answers <see langword="false"/> and becomes audible at its first piece of content.
    /// </param>
    /// <param name="cancellationToken">The token of the host.</param>
    /// <returns>The source the run reads. The caller ends it with <see cref="EndRun"/>.</returns>
    private CancellationTokenSource StartRun(bool audibleFromTheStart, CancellationToken cancellationToken)
    {
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (_interruptLock)
        {
            _interruption = null;
            _runCancellation = cancellation;
            _runIsAudible = audibleFromTheStart;
        }

        return cancellation;
    }

    /// <summary>Closes the window, and frees the session for the next turn.</summary>
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

    /// <summary>Records a barge-in against the turn that finished last, and corrects its record.</summary>
    /// <param name="utteranceUntilInterrupt">The text the caller actually heard.</param>
    /// <param name="durationUntilInterrupt">How much of the reply played, as the relay reported it.</param>
    /// <returns>
    /// <see langword="true"/> when the turn was amended, and <see langword="false"/> when there was
    /// no turn to amend.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The caller holds <see cref="_interruptLock"/>. That is what keeps this rewrite of the
    /// transcript apart from the one <see cref="CompleteTurnAsync"/> makes: a held prompt can have
    /// the next turn already running when this lands, and the two would otherwise write the same
    /// list at once.
    /// </para>
    /// <para>
    /// The span is replaced rather than truncated, because the next turn's own spoken message may
    /// already sit behind it. What replaces it is exactly what the in-flight path of
    /// <see cref="CompleteTurnAsync"/> would have written: the tool pairs that finished, then one
    /// assistant message holding the heard text, and none at all when the caller heard nothing.
    /// </para>
    /// <para>
    /// Neither vendor value is touched beyond the trim of correction 2 of section 10, which pipecat
    /// pins in a test of its own. Nothing here estimates, rounds, or clamps either one: that is D28.
    /// </para>
    /// </remarks>
    private bool AmendLastTurn(string utteranceUntilInterrupt, TimeSpan durationUntilInterrupt)
    {
        // The chain has already closed, so nothing may be appended behind call.ended.
        if (Volatile.Read(ref _ended) == 1
            || _amendable is not { } amendable
            || LastTurn is not { InterruptedAfter: null } finished)
        {
            return false;
        }

        var heard = utteranceUntilInterrupt.Trim();

        List<ChatMessage> corrected = [.. FinishedToolMessages(amendable.Messages)];
        if (heard.Length > 0)
        {
            // A barge-in inside the first 100 ms leaves no heard text, and an empty assistant
            // message teaches the model nothing. pipecat and livekit both guard this.
            corrected.Add(new ChatMessage(ChatRole.Assistant, heard));
        }

        _transcript.RemoveRange(amendable.TranscriptStart, amendable.TranscriptLength);
        _transcript.InsertRange(amendable.TranscriptStart, corrected);
        _amendable = amendable with { TranscriptLength = corrected.Count };

        LastTurn = finished with { ReplyText = heard, InterruptedAfter = durationUntilInterrupt };

        Append(NewEvent(
            AuditEventKind.ReplyInterrupted,
            _time.GetUtcNow(),
            finished.TurnIndex,
            amends: amendable.CompletedSequence,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.UtteranceUntilInterrupt] = heard,
                [AuditPayloadKeys.DurationUntilInterruptMs] =
                    ((long)durationUntilInterrupt.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            }));

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
    /// <para>
    /// This is where the chain of D23 is written, because this is where the turn index, both stages,
    /// and the moment the turn ended are all known at once. The clock is read exactly once, so every
    /// event of the turn carries the same instant.
    /// </para>
    /// <para>
    /// The interruption is read twice, at the two moments it can be answered. The first read decides
    /// what this turn records for itself. The second, in the commit lock at the end, catches a
    /// barge-in that landed while the extractor was still running: <see cref="EndRun"/> does not run
    /// until this method has returned, so <see cref="Interrupt"/> in that window still finds a live
    /// run and answers <see langword="true"/>, and only the second read makes that answer true. The
    /// same lock then closes the window behind it, so the tail of this method needs no third read.
    /// </para>
    /// </remarks>
    /// <param name="refused">
    /// Whether moderation flagged what the caller said, so the model never ran. A refused turn skips
    /// the extractor: see <see cref="RefuseFlaggedPromptAsync"/>. Everything else about the turn is
    /// ordinary, so the refusal enters the transcript, the stage machine advances, and
    /// <c>turn.completed</c> records the refusal under <see cref="AuditPayloadKeys.ReplyText"/>.
    /// </param>
    private async Task<TurnResult> CompleteTurnAsync(
        Turn turn,
        AgentResponse response,
        string? toolFault,
        CancellationToken cancellationToken,
        bool refused = false)
    {
        var interruption = CurrentInterruption();
        var reply = response.Text;
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

        // What this turn adds to the transcript. It is built here and written at the end of the
        // method, in one lock with the rest of what a late barge-in may still amend: the transcript
        // span, the sequence the amendment must reference, and LastTurn itself. Nothing between here
        // and there reads the transcript — the extractor reads the finished turn, and the writers
        // read the state document — so building it now and committing it once costs nothing.
        List<ChatMessage> written;
        if (interruptedAfter is not null || failure is not null)
        {
            // The transcript holds what the caller heard. A run that stopped mid-round would otherwise
            // leave a tool call with no result behind, and the next turn would send it. So the pairs
            // that finished are kept and only an unpaired call is dropped: a side effect that ran must
            // stay visible to the next turn. livekit/agents fixed the same defect in issue 3702.
            written = [.. FinishedToolMessages(response.Messages)];

            // A barge-in inside the first 100 ms leaves no heard text, and an empty assistant message
            // teaches the model nothing. pipecat and livekit both guard this.
            if (reply.Length > 0)
            {
                written.Add(new ChatMessage(ChatRole.Assistant, reply));
            }
        }
        else
        {
            written = [.. response.Messages];
        }

        // Writer order, step 2.
        ApplyToolResults(response.Messages);

        // Writer order, step 3. The deadline is here and not on the whole method, because the audit
        // append and the stage advance must always run.
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

        var completedSequence = WriteTurnEvents(
            turn, endedAt, stageAfter, reply, spokenReply, toolFault, interruptedAfter);

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
        // which LastTurn already names this turn while the span or the sequence still names the one
        // before it would let an amendment rewrite the wrong part of the transcript.
        lock (_interruptLock)
        {
            var start = _transcript.Count;
            _transcript.AddRange(written);

            // A turn one barge-in already cut is not amendable again, so it is not published here.
            _amendable = interruptedAfter is null
                ? new AmendableTurn(completedSequence, response.Messages, start, written.Count)
                : null;
            LastTurn = result;

            // The second read. The first one happened before the extractor await, and that await is
            // bounded only by TurnCompletionTimeout, so a barge-in has five whole seconds in which
            // to land after this turn already decided it was not interrupted. It finds
            // _runCancellation still set and the run still audible, because EndRun does not run
            // until this method has returned, so it takes the in-flight path, cancels a run that has
            // already stopped, and answers true — and true, on the contract of Interrupt, means the
            // barge-in is recorded. Nothing recorded it. Reading _interruption again here, one
            // statement after _amendable and LastTurn were published, hands that frame to the very
            // path a frame arriving one instant later would have taken: the tested amendment, which
            // writes the TurnCompleted then ReplyInterrupted pair T23 asks for. The _amendable guard
            // above already fits: it is published exactly when interruptedAfter is null, which is
            // the only state this window can be reached in.
            if (interruption is null
                && _interruption is { } late
                && AmendLastTurn(late.HeardText, late.PlayedDuration)
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
            EndCall(CallEndReason.AgentCompleted, endedAt, stageAfter);
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

    /// <summary>Writes the audit events of one finished turn, in the order they happened.</summary>
    /// <param name="turn">The turn that just spoke.</param>
    /// <param name="endedAt">The moment the turn ended.</param>
    /// <param name="stageAfter">The stage the machine holds after the turn.</param>
    /// <param name="reply">The text the caller heard.</param>
    /// <param name="spokenReply">The whole reply the model produced.</param>
    /// <param name="toolFault">The message of the fault, or <see langword="null"/>.</param>
    /// <param name="interruptedAfter">The played duration, or <see langword="null"/>.</param>
    /// <returns>
    /// The sequence of the <c>turn.completed</c> event, so a barge-in that arrives after this turn
    /// already ended can name it through <see cref="AuditEvent.AmendsSequence"/>.
    /// </returns>
    /// <remarks>
    /// A barge-in writes two events and not one. T23: the chain is append-only, so an amendment is a
    /// second event that references the first, and <c>reply.interrupted</c> names the sequence of the
    /// <c>turn.completed</c> event it corrects. It carries the text the caller ACTUALLY HEARD, which
    /// the relay reported and nothing here estimated. See item 6a. A barge-in the vendor reports
    /// only after this turn ended writes the very same pair, from <see cref="AmendLastTurn"/>.
    /// </remarks>
    private long WriteTurnEvents(
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
            return completed.Sequence;
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

        return completed.Sequence;
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

    /// <summary>Reads what the caller said through moderation, and refuses the turn when it is flagged.</summary>
    /// <param name="turn">The turn that just began.</param>
    /// <param name="userInput">The words the caller spoke.</param>
    /// <param name="cancellationToken">The token of the run.</param>
    /// <returns>
    /// The refusal to answer with, or <see langword="null"/> when the turn may reach the model.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>This runs before the model, and the caller waits for it.</b> The owner decided on
    /// 2026-08-13 that moderation reads what the CALLER said and refuses the turn, rather than
    /// reading the agent's reply and recording it as section 11 item 11 asks. Recording a harmful
    /// reply protects nobody, because the caller already heard it. The design permits the wait:
    /// section 9 says "moderation is not a judge", which exempts it from the D9 rule that keeps a
    /// judge off the turn. <see cref="ModerationTimeout"/> bounds it.
    /// </para>
    /// <para>
    /// <b>It fails open.</b> An endpoint that does not answer, times out, or throws lets the turn go
    /// on. A vendor outage must not refuse every caller on a support line. The <c>unavailable</c>
    /// metric outcome is the only record that a turn went unchecked, so an operator alerts on it.
    /// </para>
    /// <para>
    /// The flag is appended BEFORE the <c>turn.completed</c> event of this turn, because the verdict
    /// is known before the model runs. It amends nothing, and
    /// <see cref="AuditEvent.TurnIndex"/> names the turn it belongs to. That is the one rule that
    /// separates <see cref="AuditEventKind.PromptFlagged"/> from
    /// <see cref="AuditEventKind.ReplyInterrupted"/>, which must amend under T23.
    /// </para>
    /// <para>
    /// A refused turn speaks <see cref="AgentCoreConfiguration.RefusalReply"/> and NOT
    /// <see cref="AgentCoreConfiguration.FallbackReply"/>. The fallback is the section 8.7 line for a
    /// turn that failed, and it asks the caller to say it again, which would invite the flagged words
    /// back. A refusal is not a failure: nothing broke, and the model was never asked.
    /// </para>
    /// </remarks>
    private async ValueTask<AgentResponse?> RefuseFlaggedPromptAsync(
        Turn turn,
        string userInput,
        CancellationToken cancellationToken)
    {
        if (_moderation is null)
        {
            return null;
        }

        IReadOnlyList<string> categories;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(ModerationTimeout);
            try
            {
                categories = await _moderation
                    .FlaggedCategoriesAsync(userInput, deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The deadline passed, and not the caller's own token. A cancel the host asked for
                // still propagates, because that is the host ending the turn and not a slow vendor.
                Log.ModerationUnavailable(_logger, CallId, turn.Index, ModerationTimedOutReason);
                AgentCoreTelemetry.RecordModeration(AgentCoreTelemetry.ModerationUnavailable);
                return null;
            }
#pragma warning disable CA1031 // Moderation guards the turn. It must never be the thing that drops it.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                Log.ModerationUnavailable(_logger, CallId, turn.Index, ModerationFaultedReason);
                AgentCoreTelemetry.RecordModeration(AgentCoreTelemetry.ModerationUnavailable);
                return null;
            }
        }

        if (categories.Count == 0)
        {
            AgentCoreTelemetry.RecordModeration(AgentCoreTelemetry.ModerationClean);
            return null;
        }

        // The order is the endpoint's, because AuditPayloadKeys.ModerationCategories promises it.
        var flagged = string.Join(',', categories);

        Append(NewEvent(
            AuditEventKind.PromptFlagged,
            _time.GetUtcNow(),
            turn.Index,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.ModerationCategories] = flagged,
            }));

        Log.PromptRefused(_logger, CallId, turn.Index, flagged);
        AgentCoreTelemetry.RecordModeration(AgentCoreTelemetry.ModerationFlagged);

        return new AgentResponse(new ChatMessage(ChatRole.Assistant, _compiled.Configuration.RefusalReply));
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

    /// <summary>Keeps the tool calls whose results arrived, and drops every unpaired call and every word.</summary>
    /// <param name="messages">Every message the round produced.</param>
    /// <returns>The tool content that carries a complete call-and-result pair, in its original order.</returns>
    /// <remarks>
    /// <para>
    /// A model refuses a history that holds a tool call with no result, so an interrupted round
    /// cannot simply keep everything. It must not silently forget a side effect either. The rule is
    /// therefore per call id: keep the call when its result is present.
    /// </para>
    /// <para>
    /// No prose survives, wherever it sits. A real model routinely writes a line and puts the tool
    /// call it announces on the same assistant message — "Let me check that for you", then the call
    /// — and the caller heard only as much of that line as the vendor played. The heard text is
    /// added as its own assistant message afterwards, so keeping the prose here would put the reply
    /// in the transcript twice: once as the model wrote it, once as the caller heard it.
    /// </para>
    /// </remarks>
    private static List<ChatMessage> FinishedToolMessages(IList<ChatMessage> messages)
    {
        HashSet<string> answered = [];
        foreach (var message in messages)
        {
            foreach (var result in message.Contents.OfType<FunctionResultContent>())
            {
                answered.Add(result.CallId);
            }
        }

        List<ChatMessage> kept = [];
        foreach (var message in messages)
        {
            // A parallel round can finish one call and leave a sibling call in the same message
            // mid-flight. The rule is per call id and not per message, so only the unfinished call
            // is stripped out; the finished one, whose side effect already ran, stays in place.
            List<AIContent> tools =
            [
                .. message.Contents.Where(content => content switch
                {
                    TextContent => false,
                    FunctionCallContent call => answered.Contains(call.CallId),
                    _ => true,
                }),
            ];

            if (!tools.Any(content => content is FunctionCallContent or FunctionResultContent))
            {
                // Plain prose, or a message whose every call is still in flight. Neither belongs in
                // the next turn.
                continue;
            }

            if (tools.Count == message.Contents.Count)
            {
                // Nothing was stripped, so the message is already the finished shape. Keep it whole,
                // contents and order unchanged.
                kept.Add(message);
                continue;
            }

            var trimmed = message.Clone();
            trimmed.Contents = tools;
            kept.Add(trimmed);
        }

        return kept;
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

    /// <summary>Everything a barge-in that arrives after a turn ended needs to correct that turn.</summary>
    /// <param name="CompletedSequence">
    /// The sequence of that turn's <c>turn.completed</c> event, which the amendment names through
    /// <see cref="AuditEvent.AmendsSequence"/>. T23 makes the chain append-only, so nothing is
    /// rewritten and the correction is a second event.
    /// </param>
    /// <param name="Messages">
    /// What the model produced. The amendment rebuilds the transcript span from these, so the tool
    /// pairs that finished survive the correction exactly as they survive an in-flight barge-in.
    /// </param>
    /// <param name="TranscriptStart">Where that turn's own messages begin in the transcript.</param>
    /// <param name="TranscriptLength">How many of them there are.</param>
    /// <remarks>
    /// The span is a position and a length rather than a truncation point, because the next turn's
    /// spoken message can already sit behind it: a held prompt starts the next turn while the vendor
    /// is still speaking this one, and that turn's own words must survive the correction.
    /// </remarks>
    private sealed record AmendableTurn(
        long CompletedSequence,
        IList<ChatMessage> Messages,
        int TranscriptStart,
        int TranscriptLength);

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

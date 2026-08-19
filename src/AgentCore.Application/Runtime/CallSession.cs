using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// The words of the call live in one <see cref="AgentSession"/>, opened at call start and carried
/// through every turn. One session carries a call that changes agent — measured on
/// Microsoft.Agents.AI 1.17.0 and pinned by <c>AgentSessionAcrossAgentsTests</c> — so row 2 switches
/// stage without switching transcript. <see cref="AgentCoreChatHistoryProvider"/> holds them, which
/// is what lets a turn of rows 1 and 2 send the new caller message alone: the framework asks the
/// provider for the history and prepends it.
/// </para>
/// <para>
/// <b>The turn loop still writes every row of store 1 itself</b>, because two of its rules are the
/// turn loop's alone: a turn the caller cut short holds the words the caller HEARD and only the tool
/// pairs that finished, and the <c>&lt;system-reminder&gt;</c> that rides the request must never be
/// stored. <see cref="AgentCoreChatHistoryProvider.StoreChatHistoryAsync"/> records what was
/// measured.
/// </para>
/// <para>
/// Rows 3 and 4 are the exception that shapes the seam: <c>AsAIAgent()</c>'s host agent accepts no
/// session but its own <c>WorkflowSession</c>, so a graph turn takes a fresh one of those each turn
/// and reads its history out of the request messages instead. The session the call holds is then a
/// carrier for the transcript and nothing else, and the write path is the same one every other row
/// takes — with one rule of its own: store 1 keeps the caller-facing turn, never the graph's
/// node-to-node chatter.
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
/// The session is also where the facts of the call are RAISED, because it is the only place that
/// knows the turn index, the stage before and after, and the ordinal a later amendment must
/// reference. It allocates <see cref="CallEvent.Ordinal"/> itself, counting from zero for each call,
/// and it knows nothing about what reads it: <see cref="ICallObserver"/> is the one seam, and the
/// audit chain of D23, the counters of section 8.6, and the "log once" rows of section 8.7 are three
/// readings of the same fact behind it. It never waits for any of them — section 7 measures a durable
/// insert at 13 ms p50 against 91 nanoseconds to enqueue, so the reply leaves before the row exists.
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

    /// <summary>
    /// What opens the <c>system</c> message a graph row's history rides on.
    /// </summary>
    internal const string HistoryPreamble = "Conversation so far:\n";

    /// <summary>
    /// What names the caller in that message.
    /// </summary>
    internal const string CallerLinePrefix = "Caller: ";

    /// <summary>
    /// What names this agent in that message.
    /// </summary>
    internal const string AgentLinePrefix = "You: ";

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

    private readonly CompiledAgent _compiled;
    private readonly StagePolicy? _policy;
    private readonly StateExtractor? _extractor;
    private readonly CounterStateWriter _counters;
    private readonly TimeProvider _time;
    private readonly CallObserverDispatcher _observers;
    private readonly DateTimeOffset _startedAt;
    private readonly AgentCoreChatHistoryProvider _history;
    private readonly bool _sessionCarriesHistory;
    private readonly Lock _interruptLock = new();
    private AgentSession? _agentSession;
    private CancellationTokenSource? _runCancellation;
    private Interruption? _interruption;
    private long? _amendableOrdinal;
    private long _ordinal;
    private int _running;
    private int _ended;

    // Whether the running turn has already handed the host something to speak. One rule for both
    // run shapes: a run that has handed the host nothing cannot be the turn the caller was hearing,
    // so a barge-in in that window belongs to the turn that finished before it. A streaming turn
    // raises this at its first piece of content; a turn that never streams hands the host nothing
    // until it returns, so it never raises it and is never cut in flight. See the remarks on
    // <see cref="Interrupt"/>. It is dropped twice: once in CompleteTurnAsync's commit lock, the
    // moment the turn's own record exists for a late barge-in to amend, and again in EndRun, which
    // is the only clear a turn that never reached that commit gets.
    private volatile bool _runIsAudible;

    /// <summary>Creates the session of one call.</summary>
    /// <param name="callId">The id of the call.</param>
    /// <param name="compiled">The compiled agent. It is shared by every call.</param>
    /// <param name="guards">The evaluator that runs each exit guard and each increment rule.</param>
    /// <param name="extractor">The extractor, or <see langword="null"/> when the document declares none.</param>
    /// <param name="timeProvider">The clock the reserved <c>callDurationSeconds</c> slot reads.</param>
    /// <param name="observers">
    /// Everything that watches this call, or <see langword="null"/> for a call nothing watches. One
    /// dispatcher belongs to one session: its ordering guarantee is per instance, so a shared one
    /// would make every call queue behind every other. <see cref="CallSessionFactory"/> builds it.
    /// </param>
    internal CallSession(
        string callId,
        CompiledAgent compiled,
        IGuardEvaluator guards,
        StateExtractor? extractor,
        TimeProvider timeProvider,
        CallObserverDispatcher? observers = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(guards);
        ArgumentNullException.ThrowIfNull(timeProvider);

        CallId = callId;
        _compiled = compiled;
        _history = compiled.History;

        // Rows 3 and 4 run a workflow, and its host agent takes no session but its own, so their
        // history rides the request messages instead.
        _sessionCarriesHistory =
            compiled.Shape is CompiledAgentShape.SingleAgent or CompiledAgentShape.Policy;
        _extractor = extractor;
        _counters = new CounterStateWriter(guards);
        _time = timeProvider;

        // The seam is optional and it has a working default. A host that binds nothing to watch the
        // call still answers it, and the library never throws for want of an observer.
        _observers = observers ?? new CallObserverDispatcher([]);
        _startedAt = timeProvider.GetUtcNow();

        // A document with no policy: has no stage machine. The single-agent row and both graph rows
        // read that way, and neither of them ever ends a call by itself.
        _policy = compiled.Configuration.Policy is null ? null : compiled.CreatePolicy(guards);
        State = new StateDocument(compiled.Configuration, _policy?.Stage);

        // Writer order, step 1.
        ConstStateWriter.Apply(State);

        // Ordinal 0 of the call. It started, and no turn has run.
        _ = Raise(CallEventKind.CallStarted, _startedAt, turnIndex: null);
    }

    /// <summary>Gets the id of the call.</summary>
    public string CallId { get; }

    /// <summary>Gets the stage the machine holds. It is empty when the document declares no policy.</summary>
    public string Stage => State.Stage;

    /// <summary>Gets whether the call reached a terminal stage. A document with no policy never does.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Gets the state of this call. Every guard and every increment rule reads it.</summary>
    public StateDocument State { get; }

    /// <summary>Gets the conversation, oldest first. Every stage of the call reads it.</summary>
    /// <remarks>
    /// A copy taken under the provider's own per-call lock, so what comes back is one whole
    /// conversation as it stood at one instant, and never a list a barge-in is halfway through
    /// rewriting. A call whose first turn has not started yet holds nothing.
    /// </remarks>
    public IReadOnlyList<ChatMessage> Transcript
        => Session() is { } session ? _history.Read(session) : [];

    /// <summary>Gets the turn that finished last, or <see langword="null"/> before the first turn ends.</summary>
    public TurnResult? LastTurn { get; private set; }

    /// <summary>Gets the compiled agent this session runs. Every call shares it.</summary>
    public CompiledAgent Compiled => _compiled;

    /// <summary>Runs one turn end to end, and returns what it did.</summary>
    /// <param name="userInput">What the caller said.</param>
    /// <param name="cancellationToken">Cancels the model calls.</param>
    /// <returns>The finished turn. It always carries a spoken line.</returns>
    /// <remarks>
    /// <para>
    /// A tool that fails four times in a row throws out of the run, and section 8.7 says that must
    /// never kill the call. The turn ends with <see cref="FallbackReply"/>, the writers still run in
    /// their fixed order, and the next turn of the same session starts normally.
    /// </para>
    /// <para>
    /// A barge-in never cuts this turn while it runs, because nothing of it has reached the host
    /// yet: the reply is handed over whole, at the return. <see cref="Interrupt"/> during the run
    /// amends the turn that finished last — the only turn the caller could still be hearing — and
    /// this run finishes undisturbed. That is the one audibility rule both run shapes share.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The call already reached a terminal stage, another turn of this call is still running, or the
    /// stage the machine holds names no agent.
    /// </exception>
    public async Task<TurnResult> RunTurnAsync(string userInput, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userInput);

        var turn = BeginTurn(userInput, await OpenSessionAsync(cancellationToken).ConfigureAwait(false));

        // A run that never streams hands the host NOTHING until it returns, so nothing of it can
        // have reached the caller while it runs, and a barge-in during it belongs to the turn that
        // finished last — the only turn the caller could still be hearing. It never becomes audible
        // in flight, and Interrupt never cuts it. This used to be the other way around
        // (audibleFromTheStart: true), on the claim that handing the reply over at once left no
        // inaudible window; the streaming path's own rule says the opposite, and the owner took the
        // one rule for both shapes on 2026-08-18.
        var cancellation = StartRun(cancellationToken);
        try
        {
            // Row 4 of the compile table. The compiled graph is a process singleton, so a guarded
            // edge inside it reads the state of the call that runs now, through this scope. The
            // using statement closes the scope when the turn ends, when it throws, and when it is
            // cancelled.
            using var scope = CallStateScope.Enter(State);

            // The other scope of the same shape, and open for the same reason: the chat client that
            // invokes tools is shared by every call under T44, so it finds the call it is running for
            // here. It closes with the turn, exactly as the state scope above does.
            using var faults = ToolFailureScope.Enter(failure => RecordToolFailure(turn.Index, failure));

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
                        options: ConversationOptions(),
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

        var turn = BeginTurn(userInput, await OpenSessionAsync(cancellationToken).ConfigureAwait(false));

        // A streaming turn becomes audible only once it hands the host its first piece of content.
        // Until then nothing of it has reached the caller, and a barge-in belongs elsewhere.
        var cancellation = StartRun(cancellationToken);
        try
        {
            List<AgentResponseUpdate> updates = [];
            string? toolFault = null;

            // Row 4 of the compile table. Control crosses into the compiled graph twice: here, where
            // the wrapper builds the stream, and again inside each round below. This scope covers the
            // first crossing and everything up to the first yield.
            using var opening = CallStateScope.Enter(State);

            // See the same pair in RunTurnAsync. This one covers the first crossing; the round below
            // opens it again, because an async iterator restores its caller's execution context at
            // every yield and an ambient value does not survive one.
            using var openingFaults = ToolFailureScope.Enter(failure => RecordToolFailure(turn.Index, failure));

            var stream = turn.Agent
                .RunStreamingAsync(
                    turn.Request,
                    await RunSessionAsync(turn, cancellation.Token).ConfigureAwait(false),
                    options: ConversationOptions(),
                    cancellationToken: cancellation.Token)
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
                        using var roundFaults = ToolFailureScope.Enter(
                            failure => RecordToolFailure(turn.Index, failure));

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
                    if (CarriesContent(content) && Speaks(update))
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
    /// <see cref="CallEventKind.ReplyInterrupted"/> names the ordinal of the
    /// <see cref="CallEventKind.TurnCompleted"/> fact it corrects through
    /// <see cref="CallEvent.AmendsOrdinal"/>.
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

    /// <summary>Opens the session of this call, once, and hands the same one to every later turn.</summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The session.</returns>
    /// <remarks>
    /// Rows 1 and 2 take the agent's own session, which is what carries the call through
    /// <see cref="AgentCoreChatHistoryProvider"/> and across a stage switch. Rows 3 and 4 run a
    /// workflow, whose host agent refuses any session but its own, so they take a carrier that
    /// holds the transcript and never reaches an agent.
    /// </remarks>
    private async ValueTask<AgentSession> OpenSessionAsync(CancellationToken cancellationToken)
    {
        if (Session() is { } opened)
        {
            return opened;
        }

        var session = _sessionCarriesHistory
            ? await _compiled.TurnAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false)
            : new CallHistorySession();

        lock (_interruptLock)
        {
            _agentSession ??= session;
            return _agentSession;
        }
    }

    /// <summary>Reads the session one run is handed.</summary>
    /// <param name="turn">The turn about to run.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The call's session on rows 1 and 2, and a fresh workflow session on a graph row.</returns>
    /// <remarks>
    /// A workflow host agent refuses any session but its own, and its own cannot be truncated or
    /// read, so a graph row never carries one across turns: the call rides the request messages
    /// instead. A fresh one costs 3.5–11.5 µs and about 1.4 KB, does no I/O, and measured no slower
    /// than carrying one, because a carried workflow transcript grows by three messages a turn
    /// rather than two.
    /// </remarks>
    private async ValueTask<AgentSession> RunSessionAsync(Turn turn, CancellationToken cancellationToken)
        => _sessionCarriesHistory
            ? turn.Session
            : await turn.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Reads the session of this call, or null before its first turn opened one.</summary>
    /// <returns>The session.</returns>
    /// <remarks>
    /// A barge-in and a transcript read both arrive from other threads, so the field is read under
    /// the same lock that publishes it.
    /// </remarks>
    private AgentSession? Session()
    {
        lock (_interruptLock)
        {
            return _agentSession;
        }
    }

    /// <summary>Waits for every store 1 write this call has queued.</summary>
    /// <returns>A task that completes when the words of the call are durable.</returns>
    /// <remarks>
    /// Nothing on the call path waits for a write — a turn queues its rows and speaks. Anything that
    /// must read the record back, rather than the live history, waits here first.
    /// </remarks>
    internal Task FlushTranscriptAsync()
        => Session() is { } session ? _history.DrainAsync(session) : Task.CompletedTask;

    /// <summary>Picks the agent, builds the model input, and takes the turn.</summary>
    /// <param name="userInput">What the caller said.</param>
    /// <param name="session">The session of this call.</param>
    /// <returns>Everything the rest of the turn needs.</returns>
    private Turn BeginTurn(string userInput, AgentSession session)
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

        // The reminder rides a request that happens anyway, and it rides exactly one. Store 1 keeps
        // the clean message, so a stale reminder never repeats in a later turn. It reads the state
        // document and never the transcript.
        var reminder = _policy is null ? null : UnfilledSlotReminder.Build(State, _policy.CurrentStage);
        ChatMessage spoken = new(ChatRole.User, userInput);
        ChatMessage prompt = new(ChatRole.User, UnfilledSlotReminder.Prepend(reminder, userInput));

        // The framework has never heard of a turn, so the turn loop names this one before the run.
        _history.BeginTurn(session, CallId, State.TurnIndex);

        // Rows 1 and 2 read the call out of the session, so the run carries the new message alone
        // and the provider prepends the rest. A workflow takes no provider, so its history rides
        // the request, rendered into the one role a node still recognises. Neither shape puts the
        // caller's message in store 1 yet: the turn writes what it said and what it heard together,
        // when it commits, so the run that is about to read the history does not find its own
        // prompt already in it.
        List<ChatMessage> request = _sessionCarriesHistory
            ? [prompt]
            : GraphHistory(_history.Read(session)) is { } rendered ? [rendered, prompt] : [prompt];

        // One turn is one span. The call id rides here, on a span attribute, because T61 refuses it
        // on a metric. The span is disposed in the finally of whichever run method opened the turn.
        var activity = AgentCoreTelemetry.StartTurn(CallId, State.TurnIndex, State.Stage);
        return new Turn(
            agent,
            session,
            request,
            spoken,
            State.Stage,
            State.TurnIndex,
            activity,
            _time.GetTimestamp());
    }

    /// <summary>Renders the call so far into the one role a workflow node still recognises.</summary>
    /// <param name="history">The caller-facing history of this call, oldest first.</param>
    /// <returns>One <c>system</c> message, or <see langword="null"/> on the first turn of a call.</returns>
    /// <remarks>
    /// Measured on 1.17.0: a workflow demotes every caller-supplied <c>assistant</c> message to
    /// <c>user</c> on the way into a node, so a node handed the raw history cannot tell what it said
    /// from what the caller said and reads its own answers as things the caller asked for.
    /// <c>system</c> and <c>tool</c> survive the demotion, so the conversation rides one
    /// <c>system</c> message that names both speakers instead.
    /// </remarks>
    private static ChatMessage? GraphHistory(IReadOnlyList<ChatMessage> history)
    {
        StringBuilder rendered = new();

        foreach (var message in history)
        {
            if (message.Text is not { Length: > 0 } text)
            {
                continue;
            }

            rendered
                .Append(message.Role == ChatRole.User ? CallerLinePrefix : AgentLinePrefix)
                .Append(text)
                .Append('\n');
        }

        return rendered.Length == 0
            ? null
            : new ChatMessage(ChatRole.System, HistoryPreamble + rendered.ToString().TrimEnd('\n'));
    }

    /// <summary>Names this call as the conversation the run's <c>gen_ai.conversation.id</c> reports.</summary>
    /// <returns>
    /// The run options a <see cref="ChatClientAgent"/> reads <c>ConversationId</c> off, whether or not
    /// something else wraps it. Row 1 and row 2 hand <c>turn.Agent</c> straight through to the
    /// <see cref="OpenTelemetryAgent"/> wrapping one compiled
    /// <see cref="ChatClientAgent"/>, so <c>gen_ai.conversation.id</c> lands on every <c>chat</c> and
    /// <c>invoke_agent</c> span those rows produce, for free, alongside the same value
    /// <see cref="AgentCoreTelemetry.StartTurn"/> already puts on <c>agentcore.turn</c>. Row 3 and row
    /// 4 hand this same options instance to a workflow-wrapped agent instead, and nothing here confirms
    /// a graph node forwards it on to the <see cref="ChatClientAgent"/> at that node — new plumbing to
    /// confirm or force that would be a second change, not this one.
    /// </returns>
    private ChatClientAgentRunOptions ConversationOptions()
    {
        ChatOptions options = new() { ConversationId = CallId };

        if (_sessionCarriesHistory)
        {
            // Measured on 1.17.0: a ConversationId reads to ChatClientAgent as "the service keeps
            // the history itself", and it answers by ignoring the agent's own ChatHistoryProvider
            // for the whole run — silently, so the model would see one message and no call at all.
            // An override named here is the one path that survives that check, and it is why
            // ConfigurationCompiler leaves ThrowOnChatHistoryProviderConflict off.
            options.AdditionalProperties ??= [];
            options.AdditionalProperties.Add<ChatHistoryProvider>(_history);
        }

        return new(options);
    }

    /// <summary>Opens the window in which <see cref="Interrupt"/> reaches this turn.</summary>
    /// <param name="cancellationToken">The token of the host.</param>
    /// <returns>The source the run reads. The caller ends it with <see cref="EndRun"/>.</returns>
    /// <remarks>
    /// Every run starts inaudible, whichever shape it takes: nothing of it has reached the caller
    /// yet, so a barge-in in this window belongs to the turn that finished last. A streaming run
    /// raises <see cref="_runIsAudible"/> at its first piece of content; a run that never streams
    /// hands the host nothing until it returns, so it stays inaudible for its whole life and a
    /// barge-in never cuts it.
    /// </remarks>
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
    /// The caller holds <see cref="_interruptLock"/>. That is what keeps this apart from the commit
    /// of <see cref="CompleteTurnAsync"/>: a held prompt can have the next turn already running when
    /// this lands, and the two would otherwise publish <see cref="LastTurn"/> at once.
    /// </para>
    /// <para>
    /// One row of store 1 changes, and it is the one the caller was hearing. The turn being
    /// corrected finished, so its reply is already written and its ordinal is the one the rewrite
    /// names — nothing here has to guess which turn a held prompt left the caller listening to.
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
            || _amendableOrdinal is not { } completedOrdinal
            || LastTurn is not { InterruptedAfter: null } finished
            || Session() is not { } session)
        {
            return false;
        }

        var heard = utteranceUntilInterrupt.Trim();

        // Store 1 keeps the reply row and rewrites its words, rather than replacing the turn's
        // messages. The turn it corrects ran to its end, so every tool call it made is already
        // paired and there is nothing unfinished to strip out.
        _ = _history.TruncateLastReply(session, heard, durationUntilInterrupt);

        LastTurn = finished with { ReplyText = heard, InterruptedAfter = durationUntilInterrupt };

        _ = Raise(
            CallEventKind.ReplyInterrupted,
            _time.GetUtcNow(),
            finished.TurnIndex,
            amends: completedOrdinal,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.UtteranceUntilInterrupt] = heard,
                [AuditPayloadKeys.DurationUntilInterruptMs] =
                    ((long)durationUntilInterrupt.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            });

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
    /// <remarks>
    /// <para>
    /// A graph row streams every node's words, and the caller must hear the answer alone rather than
    /// the graph deliberating its way to one. <c>CompiledAgent.SpokenBy</c> names the answering
    /// agents, and it is null on every row that runs one agent, where this check passes everything.
    /// </para>
    /// <para>
    /// An update the turn layers produced themselves carries no author and is still spoken: it is
    /// the refusal or the fallback, which belongs to the turn rather than to any node. The marker it
    /// carries is what tells it apart from a node's own update.
    /// </para>
    /// </remarks>
    private bool Speaks(AgentResponseUpdate update)
        => _compiled.SpokenBy is not { } spoken
            || (update.AuthorName is { } author && spoken.Contains(author))
            || update.AdditionalProperties?.Contains<TurnDisposition>() is true;

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
    /// This is where the facts of the turn are raised, because this is where the turn index, both
    /// stages, and the moment the turn ended are all known at once. The clock is read exactly once,
    /// so every event of the turn carries the same instant.
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
    /// <param name="disposition">
    /// What <see cref="ModerationAgent"/> and <see cref="FallbackAgent"/> reported about
    /// this turn, or <see langword="null"/> when neither layer marked it. A flagged turn skips the
    /// extractor: the words moderation flagged are its only input, and a slot filled from them would
    /// carry the flagged content into the state document and into every later prompt. Everything else
    /// about a refused turn is ordinary, so the refusal enters the transcript, the stage machine
    /// advances, and <c>turn.completed</c> records the refusal under
    /// <see cref="AuditPayloadKeys.ReplyText"/>.
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
        RaiseModeration(turn, disposition);

        // R1 reaches the turn through the fallback layer now, and a fault above every chat client —
        // a graph that matched no edge — still reaches the catch in the run methods. Both are the
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
            RaiseDiagnostic(CallEventKind.EmptyReply, _time.GetUtcNow(), turn.Index);
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

        List<ChatMessage> written = [];
        if (!_sessionCarriesHistory)
        {
            // Rows 3 and 4 record the caller-facing turn alone, so they keep none of the run's own
            // messages: a node's tool pair and a node's line to the next node are neither said nor
            // heard. The write below takes the heard reply straight.
        }
        else if (interruptedAfter is not null || failure is not null)
        {
            // The transcript holds what the caller heard. A run that stopped mid-round would otherwise
            // leave a tool call with no result behind, and the next turn would send it. So the pairs
            // that finished are kept and only an unpaired call is dropped: a side effect that ran must
            // stay visible to the next turn. livekit/agents fixed the same defect in issue 3702.
            written = [.. FinishedToolMessages(response.Messages)];

            if (heard is not null)
            {
                written.Add(heard);
            }
        }
        else
        {
            written = [.. response.Messages];
        }

        // Writer order, step 2.
        ApplyToolResults(response.Messages);

        // Writer order, step 3. The deadline is here and not on the whole method, because the raise
        // of the turn's events and the stage advance must always run.
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
            RaiseDiagnostic(
                CallEventKind.ExtractionFailed,
                _time.GetUtcNow(),
                turn.Index,
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [CallEventPayloadKeys.Reason] = extractionFailure,
                });
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

        var completedOrdinal = WriteTurnEvents(
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
        // which LastTurn already names this turn while the span or the ordinal still names the one
        // before it would let an amendment rewrite the wrong part of the transcript.
        lock (_interruptLock)
        {
            // What the caller said and what the caller heard, written together. The prompt the run
            // read carried a reminder and this message does not, which is why store 1 takes the
            // turn's own copy rather than the messages the framework saw.
            if (_sessionCarriesHistory)
            {
                _history.AppendTurn(turn.Session, [turn.Spoken, .. written]);
            }
            else
            {
                // Rows 3 and 4 answer with one reply per node, and the caller hears one of them.
                // Store 1 holds the caller-facing turn alone, so the deliberation that produced the
                // answer never enters the record and never reaches a later turn.
                _history.AppendCallerFacingTurn(turn.Session, turn.Spoken, heard);
            }

            // A turn one barge-in already cut is not amendable again, so it is not published here.
            _amendableOrdinal = interruptedAfter is null ? completedOrdinal : null;
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
            // writes the TurnCompleted then ReplyInterrupted pair T23 asks for. The _amendableOrdinal
            // guard above already fits: it is published exactly when interruptedAfter is null, which
            // is the only state this window can be reached in.
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

    /// <summary>Raises the durable facts of one finished turn, in the order they happened.</summary>
    /// <param name="turn">The turn that just spoke.</param>
    /// <param name="endedAt">The moment the turn ended.</param>
    /// <param name="stageAfter">The stage the machine holds after the turn.</param>
    /// <param name="reply">The text the caller heard.</param>
    /// <param name="spokenReply">The whole reply the model produced.</param>
    /// <param name="toolFault">The message of the fault, or <see langword="null"/>.</param>
    /// <param name="interruptedAfter">The played duration, or <see langword="null"/>.</param>
    /// <returns>
    /// The ordinal of the <c>turn.completed</c> fact, so a barge-in that arrives after this turn
    /// already ended can name it through <see cref="CallEvent.AmendsOrdinal"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A barge-in raises two facts and not one. T23: the chain is append-only, so an amendment is a
    /// second event that references the first, and <c>reply.interrupted</c> names the ordinal of the
    /// <c>turn.completed</c> event it corrects. It carries the text the caller ACTUALLY HEARD, which
    /// the relay reported and nothing here estimated. See item 6a. A barge-in the vendor reports
    /// only after this turn ended raises the very same pair, from <see cref="AmendLastTurn"/>.
    /// </para>
    /// <para>
    /// The tool fault of section 8.7 row six is raised here rather than where the run caught it,
    /// because the chain stores it: every stored fact of one turn carries the single
    /// <paramref name="endedAt"/> read, and its ordinal is allocated in the order the chain holds
    /// the facts.
    /// </para>
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
            // Section 8.7 row six, at TURN altitude: this run ended, the caller heard the fallback,
            // and the call lives. It is a different fact from the tool calls that failed inside it,
            // which AuditingFunctionInvokingChatClient named one by one as they happened, and it is
            // the only fact that says the TURN ended — no per-call row says that, and a fault that
            // was never a tool call at all, such as the model transport, reaches only here.
            //
            // It carries no toolName and no toolCallId, and that absence is load-bearing rather than
            // a gap. The fault arrives through ExceptionDispatchInfo with no function name on it, so
            // a missing fact stays an absent key rather than reading "unknown". The same absence is
            // what tells the two altitudes apart: LoggingCallObserver writes the one "log once" line
            // of row six for this row and stays silent for the per-call rows, so an operator still
            // reads exactly one line for one turn.
            _ = Raise(
                CallEventKind.ToolFailed,
                endedAt,
                turn.Index,
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.ToolError] = toolFault,
                });
        }

        var completed = Raise(
            CallEventKind.TurnCompleted,
            endedAt,
            turn.Index,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.ReplyText] = spokenReply,
                [AuditPayloadKeys.StageBefore] = turn.StageBefore,
                [AuditPayloadKeys.StageAfter] = stageAfter,
            });

        if (interruptedAfter is not { } played)
        {
            return completed;
        }

        _ = Raise(
            CallEventKind.ReplyInterrupted,
            endedAt,
            turn.Index,
            amends: completed,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.UtteranceUntilInterrupt] = reply,
                [AuditPayloadKeys.DurationUntilInterruptMs] =
                    ((long)played.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            });

        return completed;
    }

    /// <summary>Raises the fact of one tool call that did not run to completion.</summary>
    /// <param name="turnIndex">The turn the call belongs to.</param>
    /// <param name="failure">What the function-invocation loop saw.</param>
    /// <remarks>
    /// <para>
    /// It is raised the moment it happens rather than at the end of the turn, so the ordinal it takes
    /// sits before the <c>turn.completed</c> of the same turn and a reader walks the turn in the order
    /// it ran. The clock is read here for the same reason: the failure happened now, and the turn may
    /// still run for several more rounds.
    /// </para>
    /// <para>
    /// It is called on the framework's own tool flow, which is not the flow the turn is awaiting.
    /// Nothing here needs a lock for that: the ordinal is taken with an interlocked increment, the
    /// dispatch never blocks and never propagates, and one session runs one turn at a time so no
    /// second turn is allocating ordinals beside it.
    /// </para>
    /// <para>
    /// Every fact of the failure goes in, because §9 makes this chain the only long-term record and
    /// nothing else holds any of them. The tool call id is what tells two calls to the same tool in
    /// one turn apart, and <see cref="AuditPayloadKeys.ToolFailureKind"/> is what lets a report count
    /// an invented tool name apart from a tool that ran and threw.
    /// </para>
    /// </remarks>
    private void RecordToolFailure(int turnIndex, ToolFailure failure)
        => _ = Raise(
            CallEventKind.ToolFailed,
            _time.GetUtcNow(),
            turnIndex,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.ToolName] = failure.ToolName,
                [AuditPayloadKeys.ToolCallId] = failure.ToolCallId,
                [AuditPayloadKeys.ToolFailureKind] = ToolFailureKinds.ToToken(failure.Kind),
                [AuditPayloadKeys.ToolError] = failure.Message,
            });

    /// <summary>Raises one durable fact of this call, and takes the next ordinal.</summary>
    /// <param name="kind">What happened. The chain of D23 stores this one.</param>
    /// <param name="occurredAt">When it happened.</param>
    /// <param name="turnIndex">The turn it belongs to, or <see langword="null"/> for a call fact.</param>
    /// <param name="amends">The ordinal this fact corrects, or <see langword="null"/>.</param>
    /// <param name="payload">The detail the fact carries.</param>
    /// <returns>The ordinal this fact took, so a later amendment can name it.</returns>
    /// <remarks>
    /// The session allocates the ordinal and not the sink, because the sink answers long after the
    /// turn moved on. The counter is monotonic within one call and starts at zero, and a
    /// diagnostic-only fact takes none, which is what keeps it gap-free.
    /// </remarks>
    private long Raise(
        CallEventKind kind,
        DateTimeOffset occurredAt,
        int? turnIndex,
        long? amends = null,
        IReadOnlyDictionary<string, string>? payload = null)
    {
        var ordinal = Interlocked.Increment(ref _ordinal) - 1;

        Dispatch(kind, occurredAt, turnIndex, ordinal, amends, payload);

        return ordinal;
    }

    /// <summary>Raises one fact that is counted and logged and stored nowhere.</summary>
    /// <param name="kind">What happened. The chain of D23 holds no row for it.</param>
    /// <param name="occurredAt">When it happened.</param>
    /// <param name="turnIndex">The turn it belongs to.</param>
    /// <param name="payload">The detail the fact carries.</param>
    /// <remarks>
    /// It takes no ordinal on purpose. A diagnostic fact that consumed a number would leave a gap in
    /// the chain the moment nothing stored it, so <see cref="CallEvent.Ordinal"/> stays null and the
    /// numbers are the ones the chain held before the hook existed.
    /// </remarks>
    private void RaiseDiagnostic(
        CallEventKind kind,
        DateTimeOffset occurredAt,
        int? turnIndex,
        IReadOnlyDictionary<string, string>? payload = null)
        => Dispatch(kind, occurredAt, turnIndex, ordinal: null, amends: null, payload);

    /// <summary>Hands one fact to everything watching the call, and never waits for it.</summary>
    /// <param name="kind">What happened.</param>
    /// <param name="occurredAt">When it happened.</param>
    /// <param name="turnIndex">The turn it belongs to, or <see langword="null"/>.</param>
    /// <param name="ordinal">The number it took, or <see langword="null"/> when it took none.</param>
    /// <param name="amends">The ordinal it corrects, or <see langword="null"/>.</param>
    /// <param name="payload">The detail it carries.</param>
    /// <remarks>
    /// Nothing here propagates and nothing here blocks: <see cref="CallObserverDispatcher"/> owns
    /// both rules, once, for every observer. Section 7 measures a durable insert at 13 ms p50 and 32
    /// ms p99 against 91 nanoseconds to enqueue, so <b>an observer must never sit on the turn</b>,
    /// and one that throws is reported once while the turn goes on.
    /// </remarks>
    private void Dispatch(
        CallEventKind kind,
        DateTimeOffset occurredAt,
        int? turnIndex,
        long? ordinal,
        long? amends,
        IReadOnlyDictionary<string, string>? payload)
        => _observers.Dispatch(new CallEvent
        {
            CallId = CallId,
            Kind = kind,
            OccurredAt = occurredAt,
            Ordinal = ordinal,
            TurnIndex = turnIndex,
            AmendsOrdinal = amends,
            Payload = payload ?? new Dictionary<string, string>(StringComparer.Ordinal),
        });

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

        _ = Raise(CallEventKind.CallEnded, endedAt, turnIndex: null, payload: payload);

        return true;
    }

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
    /// <remarks>
    /// <b>The read site differs by run shape, and that is measured rather than a style choice.</b>
    /// Update-level properties do not survive streaming coalescing, so the folded response carries
    /// nothing and the raw updates carry everything. Two updates may be marked — moderation leads
    /// with an empty one and the fallback layer may close with its own — so the last one wins, which
    /// is the one that already folded the other in.
    /// </remarks>
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

    /// <summary>Raises the moderation facts of one turn, before the turn's own events.</summary>
    /// <param name="turn">The turn that just ran.</param>
    /// <param name="disposition">What the layers reported, or <see langword="null"/>.</param>
    /// <remarks>
    /// <para>
    /// <b>The owner decided on 2026-08-13 that moderation reads what the CALLER said and refuses the
    /// turn</b>, rather than reading the agent's reply and recording it as section 11 item 11 asks.
    /// Recording a harmful reply protects nobody, because the caller already heard it.
    /// </para>
    /// <para>
    /// The flag is raised BEFORE the <c>turn.completed</c> fact of this turn, and it therefore takes
    /// the lower ordinal, because the verdict is known before the model runs. It amends nothing, and
    /// <see cref="CallEvent.TurnIndex"/> names the turn it belongs to. That is the one rule that
    /// separates <see cref="CallEventKind.PromptFlagged"/> from
    /// <see cref="CallEventKind.ReplyInterrupted"/>, which must amend under T23.
    /// </para>
    /// <para>
    /// A refused turn speaks <see cref="AgentCoreConfiguration.RefusalReply"/> and NOT
    /// <see cref="AgentCoreConfiguration.FallbackReply"/>. The fallback is the section 8.7 line for a
    /// turn that failed, and it asks the caller to say it again, which would invite the flagged words
    /// back. A refusal is not a failure: nothing broke, and the model was never asked.
    /// </para>
    /// <para>
    /// A host that moderates nothing compiles no <see cref="ModerationAgent"/>, so the outcome
    /// is null and no moderation fact is raised at all.
    /// </para>
    /// </remarks>
    private void RaiseModeration(Turn turn, TurnDisposition? disposition)
    {
        switch (disposition?.Moderation)
        {
            case ModerationOutcome.Flagged:
                // One fact, three readings: the row of D23, the flagged count of section 8.6, and the
                // refusal line of section 8.7 all come from this one raise.
                _ = Raise(
                    CallEventKind.PromptFlagged,
                    _time.GetUtcNow(),
                    turn.Index,
                    payload: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [AuditPayloadKeys.ModerationCategories] = disposition.FlaggedCategories ?? string.Empty,
                    });
                break;

            case ModerationOutcome.Unavailable:
                RaiseUnavailable(turn, disposition.ModerationReason ?? ModerationFaultedReason);
                break;

            case ModerationOutcome.Clean:
                // Counted and not even logged. The clean verdict is what makes the flagged count
                // readable as a rate, and a clean turn is not a fact about the call worth a row.
                RaiseDiagnostic(CallEventKind.ModerationClean, _time.GetUtcNow(), turn.Index);
                break;

            default:
                break;
        }
    }

    /// <summary>Reports that the turn ran unchecked, because moderation could not answer.</summary>
    /// <param name="turn">The turn that ran unchecked.</param>
    /// <param name="reason">Why the endpoint did not answer, in the words the turn loop uses.</param>
    /// <remarks>
    /// Diagnostic only: it is counted and logged and stored nowhere, so it takes no ordinal.
    /// Moderation fails open, and this fact is the only record that a turn went unchecked, so an
    /// operator alerts on the metric beside the line.
    /// </remarks>
    private void RaiseUnavailable(Turn turn, string reason)
        => RaiseDiagnostic(
            CallEventKind.ModerationUnavailable,
            _time.GetUtcNow(),
            turn.Index,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CallEventPayloadKeys.Reason] = reason,
            });

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
    /// <param name="Session">The session of the call. Store 1 lives in it.</param>
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
        AgentSession Session,
        List<ChatMessage> Request,
        ChatMessage Spoken,
        string StageBefore,
        int Index,
        Activity? Activity,
        long StartedAt);

    /// <summary>The session a graph row's call holds, so store 1 has somewhere to live.</summary>
    /// <remarks>
    /// <c>AsAIAgent()</c>'s host agent refuses any session but its own <c>WorkflowSession</c>, and a
    /// workflow accepts no chat history provider, so nothing on rows 3 and 4 ever hands this to an
    /// agent. It carries the state bag <see cref="AgentCoreChatHistoryProvider"/> keeps the call in,
    /// and nothing else.
    /// </remarks>
    private sealed class CallHistorySession : AgentSession;

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

using System.Diagnostics;
using System.Globalization;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.State;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

// The prepare half of the turn loop (OpenAI turn_preparation seam): guards, withdrawal,
// request building, and the non-streaming run core. Owns no committed state; everything it
// reads lives on the owning CallSession, which keeps the single live-state owner of Phase 1.
internal sealed class CallTurnRunner
{
    private readonly CallSession _session;

    private (string ToolId, IReadOnlyList<AITool> Tools)? _delegatedTools;

    private bool _hasScreen;

    internal CallTurnRunner(CallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>
    /// Gives the runs this call delegates through one tool a set of tools of their own.
    /// </summary>
    /// <param name="delegatingToolId">The <c>kind: agent</c> tool whose delegated runs are offered these.</param>
    /// <param name="tools">The tools. An empty list offers nothing, exactly as never calling this does.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    internal void SetDelegatedTools(string delegatingToolId, IReadOnlyList<AITool> tools)
    {
        ArgumentNullException.ThrowIfNull(delegatingToolId);
        ArgumentNullException.ThrowIfNull(tools);

        _delegatedTools = (delegatingToolId, tools);
    }

    /// <summary>
    /// Gives this call the screen its tools draw on, or takes it away.
    /// </summary>
    internal void SetHasScreen(bool hasScreen) => _hasScreen = hasScreen;

    /// <summary>Runs one turn of the call against the session the call holds.</summary>
    /// <param name="userInput">What the caller said or answered.</param>
    /// <param name="origin">Where the turn hangs, or null for a caller that does not say.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <returns>What the turn did.</returns>
    internal async Task<TurnResult> RunTurnCoreAsync(
        ChatMessage userInput, CallTurnOrigin? origin, CancellationToken cancellationToken)
    {
        var session = await _session.Ledger.OpenSessionAsync(cancellationToken).ConfigureAwait(false);

        var turn = BeginTurn(userInput, session, origin);

        var cancellation = _session.Interruptions.StartRun(cancellationToken);

        try
        {
            using var ambients = EnterAmbients(turn);
            // The turn travels on the run's own options from here to the invoking client, one
            // value per turn, and in the session-keyed registry from here to the providers —
            // keyed by the session the run actually gets, which a graph row re-creates per turn.
            var invocation = TurnInvocationOf(turn);
            var runSession = await RunSessionAsync(turn, cancellation.Token).ConfigureAwait(false);
            TurnRegistry.Set(runSession, invocation);

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
                        runSession,
                        invocation.RunOptions(),
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
            return await _session.Completion.CompleteTurnAsync(turn, response, toolFault, CallTurnStream.ReadDisposition(response), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _session.Interruptions.EndRun(cancellation);
            turn.Activity?.Dispose();
        }
    }

    /// <summary>
    /// Picks the agent, withdraws whatever this turn replaces, builds the model input, and takes the
    /// turn.
    /// </summary>
    /// <param name="userInput">What the caller said or answered: words, an approval answer, or both.</param>
    /// <param name="session">The session of this call.</param>
    /// <param name="origin">Where the turn hangs, or null for a caller that does not say.</param>
    /// <returns>Everything the rest of the turn needs.</returns>
    internal CallTurn BeginTurn(ChatMessage userInput, AgentSession session, CallTurnOrigin? origin = null)
    {
        if (_session.IsComplete)
        {
            throw new InvalidOperationException(
                $"The call '{_session.CallId}' reached the terminal stage '{_session.Stage}', so it runs no further turn.");
        }

        var agent = ResolveAgent();

        // One session runs one turn at a time. The state document takes no lock, so a second turn
        // that overlapped the first would corrupt it rather than fail.
        if (!_session.Interruptions.TryEnterTurn())
        {
            throw new InvalidOperationException(
                $"A turn of the call '{_session.CallId}' is still running. One call runs one turn at a time.");
        }

        Activity? activity = null;
        try
        {
            // After both guards: a turn refused as terminal or already-running must not drop the
            // probe latch out from under the turn actually in flight. Runs exactly once here, and
            // never in EnterAmbients, which reopens per streaming step and would drop the latch
            // several times inside one streaming turn.
            _session.Clarifications.BeginTurn();

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
            var reminder = _session.Policy is null ? null : UnfilledSlotReminder.Build(_session.State, _session.Policy.CurrentStage);
            ChatMessage spoken = userInput;

            // The framework has never heard of a turn, so the turn loop names this one before the run.
            _session.History.BeginTurn(session, _session.State.TurnIndex);

            // Rows 1 and 2 read the call out of the session, so the run carries the new message alone
            // and the provider prepends the rest. A graph row that reuses its session reads the same
            // way: the resumed conversation already carries what came before. Any other graph row
            // takes no provider, so its history rides the request, rendered into the one role a node
            // still recognises. Neither shape puts the caller's message in store 1 yet: the turn
            // writes what it said and what it heard together, when it commits, so the run that is
            // about to read the history does not find its own prompt already in it.
            List<ChatMessage> request = _session.SessionCarriesHistory || _session.ReusesGraphSession
                ? [spoken]
                : TurnMessages.GraphHistory(_session.History.Read(session)) is { } rendered ? [rendered, spoken] : [spoken];

            // One turn is one span. The call id rides here, on a span attribute, because T61 refuses it
            // on a metric. The span is disposed in the finally of whichever run method opened the turn.
            activity = AgentCoreTelemetry.StartTurn(_session.CallId, _session.State.TurnIndex, _session.State.Stage);
            var knowledge = StateKnowledgeScope.Compose(
                _session.State, _session.Compiled.Configuration.Providers?.Knowledge?.Scope, _session.Scope);

            if (knowledge is { Origins.Count: > 0 })
            {
                Log.KnowledgeScopeComposed(_session.Logger, _session.CallId, _session.State.TurnIndex, knowledge.Origins);
            }

            return new CallTurn(
                agent,
                session,
                request,
                spoken,
                reminder,
                _session.State.Stage,
                _session.State.TurnIndex,
                activity,
                _session.Time.GetTimestamp(),
                _hasScreen ? new TurnRenders() : null,
                new TurnSources(),
                new TurnResults(),
                knowledge,
                origin?.MessageId);
        }
        catch
        {
            // Only the run methods' own finally frees the session and closes the span, and it starts
            // after this method returns. A throw while the turn is still being built would otherwise
            // leave _running set, so every later turn of the call is refused as one already running.
            activity?.Dispose();
            _session.Interruptions.ReleaseTurn();
            throw;
        }
    }

    /// <summary>Takes back everything the call said after the message this turn hangs off.</summary>
    internal void WithdrawSuperseded(AgentSession session, CallTurnOrigin? origin)
    {
        if (origin is not { NamesParent: true }
            || _session.History.TruncateFrom(session, origin.ParentMessageId) is not { } withdrawn)
        {
            return;
        }

        // Without this, lastNamed would survive a withdrawal that deleted the very turns it recorded,
        // and silence the probe forever about a question the caller edited away. The ask counter is
        // deliberately untouched here: what the caller heard, they still heard, and clearing it
        // would let the withdrawn segment buy a fresh maxAsks budget.
        _session.Clarifications.Withdraw();

        // The turn index the event is filed under is the one about to run; the payload is what says
        // which turns it replaced. The rows of those turns are already deleted, so nothing else in
        // any store can answer that afterwards.
        _ = _session.Events.Raise(
            CallEventKind.TurnSuperseded,
            _session.Time.GetUtcNow(),
            _session.State.TurnIndex,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuditPayloadKeys.WithdrewFromTurnIndex] =
                    withdrawn.First.ToString(CultureInfo.InvariantCulture),
                [AuditPayloadKeys.WithdrewThroughTurnIndex] =
                    withdrawn.Last.ToString(CultureInfo.InvariantCulture),
            });
    }

    /// <summary>Picks the agent that speaks this turn.</summary>
    /// <returns>The agent.</returns>
    internal AIAgent ResolveAgent()
    {
        if (_session.Policy is null)
        {
            // Row 1, row 3, and row 4 of the compile table. One entry agent answers every turn.
            return _session.Compiled.TurnAgent;
        }

        if (_session.Policy.CurrentAgentId is not { Length: > 0 }
            || _session.Compiled.TurnAgentForStage(_session.Policy.Stage) is not { } agent)
        {
            throw new InvalidOperationException(
                $"The stage '{_session.Policy.Stage}' of the call '{_session.CallId}' names no agent, so no turn can run.");
        }

        return agent;
    }

    /// <summary>
    /// Reads the session one run is handed.
    /// </summary>
    /// <param name="turn">The turn about to run.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The call's session on rows 1 and 2, the call's shared workflow session on a graph row that reuses one, and a fresh workflow session on any other graph row.</returns>
    internal async ValueTask<AgentSession> RunSessionAsync(CallTurn turn, CancellationToken cancellationToken)
    {
        if (_session.SessionCarriesHistory)
        {
            return turn.Session;
        }

        if (_session.ReusesGraphSession)
        {
            return _session.GraphSession ??= await turn.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        return await turn.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the state scope this turn runs under, so guarded graph edges read this call.
    /// </summary>
    /// <param name="turn">The turn about to run.</param>
    /// <returns>The scope. Disposing it closes the state scope.</returns>
    internal IDisposable EnterAmbients(CallTurn turn) => CallStateScope.Enter(_session.State);

    /// <summary>Builds the invocation one turn hands its tools.</summary>
    /// <param name="turn">The turn about to run.</param>
    /// <returns>Everything a tool of this turn may need, as one explicit value.</returns>
    internal TurnInvocation TurnInvocationOf(CallTurn turn)
        => new()
        {
            CallId = _session.CallId,
            TurnIndex = turn.Index,
            Stage = turn.StageBefore,
            Instructions = turn.Reminder,
            Workspace = _session.Workspace,
            Shells = _session.Shells,
            Knowledge = turn.Knowledge,
            Clarifications = _session.Clarifications,
            CarriesHistory = _session.SessionCarriesHistory,
            Screen = turn.Renders,
            Sources = turn.Sources,
            Renders = turn.Renders,
            Results = turn.Results,
            OnToolFailure = failure => _session.Events.RaiseToolFailure(turn.Index, failure),
            Tools = _delegatedTools?.Tools,
            ToolsFor = _delegatedTools?.ToolId,
        };
}

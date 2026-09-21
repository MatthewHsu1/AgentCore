using System.Diagnostics;
using System.Globalization;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.State;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime
{
    internal sealed class ConversationTurnRunner
    {
        private readonly ConversationSession _session;

        private (string ToolId, IReadOnlyList<AITool> Tools)? _delegatedTools;

        private bool _hasScreen;

        private TimeZoneInfo? _timeZone;

        internal ConversationTurnRunner(ConversationSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            _session = session;
        }

        /// <summary>
        /// Gives the runs this conversation delegates through one tool a set of tools of their own.
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
        /// Gives this conversation the screen its tools draw on, or takes it away.
        /// </summary>
        internal void SetHasScreen(bool hasScreen)
        {
            _hasScreen = hasScreen;
        }

        /// <summary>
        /// Tells this conversation which zone the person is in, so the clock line reads their date.
        /// </summary>
        internal void SetTimeZone(TimeZoneInfo zone)
        {
            _timeZone = zone;
        }

        /// <summary>Runs one turn of the conversation against the session the conversation holds.</summary>
        /// <param name="userInput">What the caller said or answered.</param>
        /// <param name="origin">Where the turn hangs, or null for a caller that does not say.</param>
        /// <param name="cancellationToken">Cancels the turn.</param>
        /// <returns>What the turn did.</returns>
        internal async Task<TurnResult> RunTurnCoreAsync(
            ChatMessage userInput, ConversationTurnOrigin? origin, CancellationToken cancellationToken)
        {
            AgentSession session = await _session.Ledger.OpenSessionAsync(cancellationToken).ConfigureAwait(false);

            await AdmitTurnAsync(session, origin, cancellationToken).ConfigureAwait(false);
            ConversationTurn turn = BeginTurn(userInput, session, origin);

            CancellationTokenSource cancellation = _session.Interruptions.StartRun(cancellationToken);

            try
            {
                TurnInvocation invocation = TurnInvocationOf(turn);

                AgentSession runSession = await OpenRunAsync(turn, cancellation.Token).ConfigureAwait(false);

                TurnRegistry.Set(runSession, invocation);

                AgentResponse response;
                string? toolFault = null;

                try
                {
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
                    toolFault = exception.Message;
                    response = new AgentResponse();
                }

                return await _session.Completion.CompleteTurnAsync(turn, response, toolFault, ConversationTurnStream.ReadDisposition(response), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _session.Interruptions.EndRun(cancellation);
                turn.Activity?.Dispose();
            }
        }

        /// <summary>
        /// Admits one turn: refuses a terminal or running conversation, then withdraws whatever the turn
        /// replaces. <see cref="BeginTurn"/> follows, from the same frame.
        /// </summary>
        /// <param name="session">The session of this conversation.</param>
        /// <param name="origin">Where the turn hangs, or null for a caller that does not say.</param>
        /// <param name="cancellationToken">Cancels the withdrawal.</param>
        internal async ValueTask AdmitTurnAsync(AgentSession session, ConversationTurnOrigin? origin, CancellationToken cancellationToken)
        {
            if (_session.IsComplete)
            {
                throw new InvalidOperationException(
                    $"The conversation '{_session.ConversationId}' reached the terminal stage '{_session.Stage}', so it runs no further turn.");
            }

            if (!_session.Interruptions.TryEnterTurn())
            {
                throw new InvalidOperationException(
                    $"A turn of the conversation '{_session.ConversationId}' is still running. One conversation runs one turn at a time.");
            }

            try
            {
                _session.Clarifications.BeginTurn();

                await WithdrawSupersededAsync(session, origin, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _session.Interruptions.ReleaseTurn();

                throw;
            }
        }

        /// <summary>
        /// Picks the agent, builds the model input, and takes the turn <see cref="AdmitTurnAsync"/> admitted.
        /// Synchronous on purpose: the turn span it opens must be the ambient activity of the caller's
        /// frame, and an async method hands no <see cref="Activity.Current"/> back.
        /// </summary>
        /// <param name="userInput">What the caller said or answered: words, an approval answer, or both.</param>
        /// <param name="session">The session of this conversation.</param>
        /// <param name="origin">Where the turn hangs, or null for a caller that does not say.</param>
        /// <returns>Everything the rest of the turn needs.</returns>
        internal ConversationTurn BeginTurn(ChatMessage userInput, AgentSession session, ConversationTurnOrigin? origin = null)
        {
            Activity? activity = null;
            try
            {
                AIAgent agent = ResolveAgent();

                string? reminder = _session.Policy is null ? null : UnfilledSlotReminder.Build(_session.State, _session.Policy.CurrentStage);

                ChatMessage spoken = userInput;

                _session.History.BeginTurn(session, _session.State.TurnIndex);

                List<ChatMessage> request = [spoken];
                if (!_session.SessionCarriesHistory
                    && !_session.ReusesGraphSession
                    && TurnMessages.GraphHistory(_session.History.Read(session)) is { } rendered)
                {
                    request = [rendered, spoken];
                }

                activity = AgentCoreTelemetry.StartTurn(_session.ConversationId, _session.State.TurnIndex, _session.State.Stage);

                KnowledgeScope? knowledge = StateKnowledgeScope.Compose(
                    _session.State, _session.Compiled.Configuration.Providers?.Knowledge?.Scope, _session.Scope);

                if (knowledge is { Origins.Count: > 0 })
                {
                    Log.KnowledgeScopeComposed(_session.Logger, _session.ConversationId, _session.State.TurnIndex, knowledge.Origins);
                }

                return new ConversationTurn(
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
                    new TurnFiles(),
                    knowledge,
                    origin?.MessageId);
            }
            catch
            {
                activity?.Dispose();

                _session.Interruptions.ReleaseTurn();

                throw;
            }
        }

        /// <summary>Takes back everything the conversation said after the message this turn hangs off.</summary>
        internal async ValueTask WithdrawSupersededAsync(AgentSession session, ConversationTurnOrigin? origin, CancellationToken cancellationToken)
        {
            if (origin is not { NamesParent: true })
            {
                return;
            }

            WithdrawnTurns? cut = _session.History.CanTruncateFrom(session, origin.ParentMessageId)
                ? _session.History.TruncateFrom(session, origin.ParentMessageId)
                : await _session.Ledger.CutUnderSummaryAsync(session, origin.ParentMessageId, cancellationToken).ConfigureAwait(false);

            if (cut is not { } withdrawn)
            {
                return;
            }

            _session.Clarifications.Withdraw();

            _ = _session.Events.Raise(
                ConversationEventKind.TurnSuperseded,
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
                return _session.Compiled.TurnAgent;
            }

            if (_session.Policy.CurrentAgentId is not { Length: > 0 }
                || _session.Compiled.TurnAgentForStage(_session.Policy.Stage) is not { } agent)
            {
                throw new InvalidOperationException(
                    $"The stage '{_session.Policy.Stage}' of the conversation '{_session.ConversationId}' names no agent, so no turn can run.");
            }

            return agent;
        }

        /// <summary>
        /// Reads the session one run is handed.
        /// </summary>
        /// <param name="turn">The turn about to run.</param>
        /// <param name="cancellationToken">Cancels the open.</param>
        /// <returns>The conversation's session on rows 1 and 2, the conversation's shared workflow session on a graph row that reuses one, and a fresh workflow session on any other graph row.</returns>
        internal async ValueTask<AgentSession> OpenRunAsync(ConversationTurn turn, CancellationToken cancellationToken)
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

        /// <summary>Builds the invocation one turn hands its tools.</summary>
        /// <param name="turn">The turn about to run.</param>
        /// <returns>Everything a tool of this turn may need, as one explicit value.</returns>
        internal TurnInvocation TurnInvocationOf(ConversationTurn turn)
        {
            return new()
            {
                ConversationId = _session.ConversationId,
                TurnIndex = turn.Index,
                Stage = turn.StageBefore,
                Instructions = turn.Reminder,
                TimeZone = _timeZone,
                Workspace = _session.Workspace,
                Shells = _session.Shells,
                Knowledge = turn.Knowledge,
                Clarifications = _session.Clarifications,
                CarriesHistory = _session.SessionCarriesHistory,
                Logger = _session.Logger,
                Screen = turn.Renders,
                Sources = turn.Sources,
                Renders = turn.Renders,
                Results = turn.Results,
                Files = turn.Files,
                OnToolFailure = failure => _session.Events.RaiseToolFailure(turn.Index, failure),
                Tools = _delegatedTools?.Tools,
                ToolsFor = _delegatedTools?.ToolId,
                State = _session.State,
            };
        }
    }
}

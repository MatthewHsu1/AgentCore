using System.Diagnostics;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.State;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime
{
    internal sealed class ConversationTurnRunner
    {
        private readonly ConversationSession _session;

        private (string ToolId, IReadOnlyList<AITool> Tools)? _delegatedTools;

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
        /// Tells this conversation which zone the person is in, so the clock line reads their date.
        /// </summary>
        internal void SetTimeZone(TimeZoneInfo zone)
        {
            _timeZone = zone;
        }

        /// <summary>
        /// Admits one turn: refuses a terminal, running or disposed conversation. <see cref="BeginTurn"/> follows, from
        /// the same frame. The turn's edit runs later, in <see cref="ConversationTurnAgent"/>.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The conversation was disposed.</exception>
        /// <exception cref="InvalidOperationException">The conversation is terminal, or runs another turn.</exception>
        internal void AdmitTurn()
        {
            if (!_session.Ledger.Reads.Diverged || _session.Events.HasEnded)
            {
                RefuseIfComplete();
            }

            if (!_session.Cuts.TryEnterTurn())
            {
                TurnRefusals.Raise(_session, _session.State.TurnIndex, TurnRefusals.Running);
                throw new InvalidOperationException(
                    $"A turn of the conversation '{_session.ConversationId}' is still running. One conversation runs one turn at a time.");
            }

            _session.Clarifications.BeginTurn();
        }

        /// <summary>Refuses a turn on a conversation that reached a terminal stage.</summary>
        /// <exception cref="InvalidOperationException">The conversation reached a terminal stage.</exception>
        internal void RefuseIfComplete()
        {
            if (_session.IsComplete)
            {
                throw new InvalidOperationException(
                    $"The conversation '{_session.ConversationId}' reached the terminal stage '{_session.Stage}', so it runs no further turn.");
            }
        }

        /// <summary>
        /// Picks the agent and takes the turn <see cref="AdmitTurn"/> admitted.
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
                    spoken,
                    reminder,
                    _session.State.Stage,
                    _session.State.TurnIndex,
                    activity,
                    _session.Time.GetTimestamp(),
                    new TurnSources(),
                    new TurnResults(),
                    new TurnFiles(),
                    knowledge,
                    origin);
            }
            catch
            {
                activity?.Dispose();

                _session.Cuts.ReleaseTurn();

                throw;
            }
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

        /// <summary>Builds the invocation one turn hands its layers and its tools.</summary>
        /// <param name="turn">The turn about to run.</param>
        /// <param name="completer">The conversation's side of the turn's seal.</param>
        /// <param name="slot">Where a cut of the turn waits for the seal.</param>
        /// <returns>Everything the seal and a tool of this turn may need, as one explicit value.</returns>
        internal TurnInvocation TurnInvocationOf(ConversationTurn turn, ITurnCompleter completer, TurnCutSlot slot)
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
                Sources = turn.Sources,
                Results = turn.Results,
                Files = turn.Files,
                OnToolFailure = failure => _session.Events.RaiseToolFailure(turn.Index, failure),
                Tools = _delegatedTools?.Tools,
                ToolsFor = _delegatedTools?.ToolId,
                State = _session.State,
                HistorySession = turn.Session,
                User = turn.Spoken,
                Origin = turn.Origin,
                RendersHistory = !_session.SessionCarriesHistory && !_session.ReusesGraphSession,
                Completer = completer,
                CutSlot = slot,
                Notices = new TurnNotices(),
            };
        }
    }
}

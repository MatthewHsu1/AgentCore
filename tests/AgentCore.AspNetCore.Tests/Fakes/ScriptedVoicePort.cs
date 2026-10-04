using AgentCore.Application.Ports;
using AgentCore.Domain;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// A conversation whose turns the test streams by hand (<see cref="ScriptedVoiceTurn"/>), the C# stand-in
    /// for LiveKit's <c>tests/fake_llm.py</c>. It refuses a second turn while one runs, as the engine does, and
    /// records every cut and recut it was told about.
    /// </summary>
    internal sealed class ScriptedVoicePort : IConversationPort
    {
        private readonly Lock _gate = new();
        private readonly List<ScriptedVoiceTurn> _turns = [];
        private readonly List<(int TurnIndex, TurnCut Cut)> _cuts = [];
        private readonly List<(int TurnIndex, TurnCut Cut)> _recuts = [];
        private ScriptedVoiceTurn? _running;
        private int _started;

        public string ConversationId => "conversation-scripted-voice";

        public string Stage => string.Empty;

        public bool IsComplete => false;

        public KnowledgeScope? Scope { get; set; }

        public TurnResult? LastTurn { get; set; }

        /// <summary>Gets or sets whether a cut of the running turn ends it, as the engine's cancellation does.</summary>
        public bool EndTurnOnCut { get; set; } = true;

        /// <summary>Gets every cut this conversation was told about, in order. Turn indexes count from zero.</summary>
        public IReadOnlyList<(int TurnIndex, TurnCut Cut)> Cuts
        {
            get { lock (_gate) { return [.. _cuts]; } }
        }

        /// <summary>Gets every recut this conversation was told about, in order.</summary>
        public IReadOnlyList<(int TurnIndex, TurnCut Cut)> Recuts
        {
            get { lock (_gate) { return [.. _recuts]; } }
        }

        /// <summary>Gets a turn by the order it runs in, from one. It may be scripted before it starts. Its index is one less.</summary>
        public ScriptedVoiceTurn Turn(int ordinal)
        {
            lock (_gate)
            {
                return TurnLocked(ordinal);
            }
        }

        public Task<TurnResult> RunTurnAsync(string userInput, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("the voice layer only ever starts a turn.");
        }

        public Task<TurnResult> RunTurnMessageAsync(ChatMessage userInput, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("the voice layer only ever starts a turn.");
        }

        public IAsyncEnumerable<ChatResponseUpdate> RunTurnStreamingAsync(
            string userInput,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("the voice layer only ever starts a turn.");
        }

        public IAsyncEnumerable<ChatResponseUpdate> RunTurnMessageStreamingAsync(
            ChatMessage userInput,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("the voice layer only ever starts a turn.");
        }

        public Task<TurnRun> StartTurnAsync(
            ChatMessage userInput, ConversationTurnOrigin? origin, CancellationToken cancellationToken = default)
        {
            ScriptedVoiceTurn turn;
            int turnIndex;
            lock (_gate)
            {
                if (_running is not null)
                {
                    throw new InvalidOperationException("another turn of this conversation is still running.");
                }

                turnIndex = _started;
                turn = _running = TurnLocked(++_started);
            }

            IAsyncEnumerable<ChatResponseUpdate> updates = turn.StreamAsync(
                userInput.Text ?? string.Empty,
                () => { lock (_gate) { _running = null; } },
                cancellationToken);
            return Task.FromResult(new TurnRun(turnIndex, updates));
        }

        public bool Cut(int turnIndex, TurnCut cut)
        {
            ScriptedVoiceTurn? running;
            lock (_gate)
            {
                if (turnIndex < 0 || turnIndex >= _started)
                {
                    return false;
                }

                _cuts.Add((turnIndex, cut));
                running = turnIndex == _started - 1 ? _running : null;
            }

            if (EndTurnOnCut)
            {
                running?.End();
            }

            return true;
        }

        public bool Recut(int turnIndex, TurnCut cut)
        {
            lock (_gate)
            {
                if (turnIndex != _started - 1 || !_cuts.Exists(recorded => recorded.TurnIndex == turnIndex))
                {
                    return false;
                }

                _recuts.Add((turnIndex, cut));
                return true;
            }
        }

        private ScriptedVoiceTurn TurnLocked(int ordinal)
        {
            while (_turns.Count < ordinal)
            {
                _turns.Add(new ScriptedVoiceTurn());
            }

            return _turns[ordinal - 1];
        }
    }
}

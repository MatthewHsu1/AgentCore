using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Conversation;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.State;

namespace AgentCore.Application.Runtime.Session
{
    internal sealed class ConversationSessionStateStore
    {
        private readonly ConversationSession _session;

        internal ConversationSessionStateStore(ConversationSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            _session = session;
        }

        /// <summary>Puts back what a previous session of this conversation had, as far as the document still allows.</summary>
        internal void Restore(ConversationSessionState stored)
        {
            if (stored.Version != ConversationSessionState.CurrentVersion)
            {
                Dropped($"the stored state is version {stored.Version} and this build writes {ConversationSessionState.CurrentVersion}.");
                return;
            }

            string? refusedStage = null;

            if (_session.Policy is null)
            {
                if (stored.Stage.Length > 0)
                {
                    refusedStage = $"the entry declares no policy, so the stage '{stored.Stage}' has nowhere to go.";
                }
            }
            else if (!_session.Policy.Declares(stored.Stage))
            {
                refusedStage = $"the document no longer declares the stage '{stored.Stage}'.";
            }

            if (refusedStage is null)
            {
                _session.Policy?.RestoreStage(stored.Stage);

                _session.State.Stage = stored.Stage;

                // An end this session already took stands, whatever an older stored state says.
                _session.IsComplete = stored.IsComplete || _session.Lifetime.Ending.Requested;
                if (stored.IsComplete)
                {
                    _session.Lifetime.Ending.Restored();
                }
            }
            else
            {
                Dropped(refusedStage);
            }

            _session.Clarifications.RestoreSpent(stored.Clarifications);

            foreach (KeyValuePair<string, JsonNode?> slot in stored.Slots)
            {
                if (ReservedStateSlots.Contains(slot.Key))
                {
                    Dropped($"the slot '{slot.Key}' is reserved, and a reserved slot is never restored.");

                    continue;
                }

                if (_session.State.TryWrite(slot.Key, slot.Value?.DeepClone()))
                {
                    continue;
                }

                Dropped(
                    _session.State.Configuration.State.ContainsKey(slot.Key)
                        ? $"the slot '{slot.Key}' no longer takes the value it was stored with."
                        : $"the document no longer declares the slot '{slot.Key}'.");
            }

            void Dropped(string reason)
            {
                SessionFaults.Raise(_session, FaultKind.StateRestorePartial, turnIndex: null, reason, cause: null);
            }
        }

        /// <summary>
        /// Takes the state another session of this conversation stored after this one last ran a turn. The session
        /// then holds what a session opened on that state now would: its stage, and its slots and no others.
        /// </summary>
        /// <param name="stored">The state the conversation store holds, newer than this session's.</param>
        internal void CatchUp(ConversationSessionState stored)
        {
            if (stored.Version == ConversationSessionState.CurrentVersion)
            {
                _session.State.ClearWrittenSlots();
                _ = ConstStateWriter.Apply(_session.State);
            }

            Restore(stored);
        }

        /// <summary>
        /// Reads the state this conversation would resume from, as it stands right now. A host serializing mid-turn
        /// gets the provider state off the live bag at that instant, not a turn-boundary snapshot. The read holds the
        /// turn lock so that it never falls inside a catch-up, which swaps the session and moves the turn index
        /// together. A graph row that reuses its session instead attaches that session's last turn-end serialization.
        /// </summary>
        internal ConversationSessionState Snapshot()
        {
            lock (_session.TurnLock)
            {
                if (_session.AgentSession is null && _session.Checkpoint is { } held)
                {
                    return held;
                }

                return new()
                {
                    Stage = _session.State.Stage,
                    IsComplete = _session.IsComplete,
                    Slots = _session.State.WrittenSlots(),
                    NextTurnIndex = _session.State.TurnIndex,
                    Clarifications = _session.Clarifications.Spent(),
                    Providers = HarnessSessionState.Capture(_session.AgentSession, _session.Compiled.HarnessStateKeys),
                    WorkflowState = _session.GraphBlob,
                };
            }
        }

        /// <summary>Names the state this conversation resumes from when the conversation store holds none of its own.</summary>
        internal void Resume(ConversationSessionState stored)
        {
            ArgumentNullException.ThrowIfNull(stored);

            lock (_session.TurnLock)
            {
                if (_session.AgentSession is not null)
                {
                    throw new InvalidOperationException(
                        $"The conversation '{_session.ConversationId}' has already run a turn, and a conversation resumes only as its "
                        + "first turn opens it. Hand the state to the factory that builds the session "
                        + "instead.");
                }

                _session.Checkpoint = stored;
            }
        }
    }

}

using AgentCore.Application.Calls;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Harness;

namespace AgentCore.Application.Runtime;

// The persistable half of the session (LangGraph checkpoint seam): restoring a previous
// session's stage, slots, and ask budget onto the live document, as far as the compiled
// document still allows. Never throws a call away over a stale blob — every refusal lands as
// a diagnostic and the call runs on what restored.
internal sealed class CallSessionStateStore
{
    private readonly CallSession _session;

    internal CallSessionStateStore(CallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Puts back what a previous session of this call had, as far as the document still allows.</summary>
    internal void Restore(CallSessionState stored)
    {
        if (stored.Version != CallSessionState.CurrentVersion)
        {
            Dropped($"the stored state is version {stored.Version} and this build writes {CallSessionState.CurrentVersion}.");
            return;
        }

        string? refusedStage = null;

        if (_session.Policy is null)
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
        else if (!_session.Policy.Declares(stored.Stage))
        {
            refusedStage = $"the document no longer declares the stage '{stored.Stage}'.";
        }

        if (refusedStage is null)
        {
            // Both, and in this order. The machine is what picks the agent and what the next
            // transition fires from; the reserved slot is what the guards and the audit chain read.
            // Moving one without the other is worse than moving neither, because the call would
            // then report a stage it was not actually running in.
            _session.Policy?.RestoreStage(stored.Stage);
            _session.State.Stage = stored.Stage;

            // Only on this branch. A stored 'true' was read off a terminal stage, so restoring it
            // beside a stage that was refused would bring the call back only to have it turn every
            // turn away — the one outcome this whole method exists to avoid.
            _session.IsComplete = stored.IsComplete;
        }
        else
        {
            Dropped(refusedStage);
        }

        // Before the slots, and unconditionally: the ask budget is per call, not per session, so a
        // caller who dropped and reconnected must not buy a fresh maxAsks and hear every clarification
        // over again. A refused stage does not refuse this — the questions were still asked.
        _session.Clarifications.RestoreSpent(stored.Clarifications);

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

            if (_session.State.TryWrite(slot.Key, slot.Value?.DeepClone()))
            {
                continue;
            }

            // TryWrite answers false for two kinds of reason that cost an operator different things
            // to fix — a slot the document no longer declares, and a value its type or enum: gate
            // now refuses — so the reason says which one happened rather than making them guess.
            Dropped(
                _session.State.Configuration.State.ContainsKey(slot.Key)
                    ? $"the slot '{slot.Key}' no longer takes the value it was stored with."
                    : $"the document no longer declares the slot '{slot.Key}'.");
        }

        void Dropped(string reason) => _session.Events.RaiseDiagnostic(
            CallEventKind.StateRestorePartial,
            _session.Time.GetUtcNow(),
            turnIndex: null,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CallEventPayloadKeys.Reason] = reason,
            });
    }

    /// <summary>
    /// Reads the state this call would resume from, as it stands right now. The provider state is
    /// read off the live bag with no turn lock around it, so a host serializing mid-turn gets the
    /// bag as it stands at that instant, not a turn-boundary snapshot. A graph row that reuses
    /// its session instead attaches that session's last turn-end serialization.
    /// </summary>
    internal CallSessionState Snapshot()
    {
        lock (_session.InterruptLock)
        {
            if (_session.AgentSession is null && _session.Checkpoint is { } held)
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
            Stage = _session.State.Stage,
            IsComplete = _session.IsComplete,
            Slots = _session.State.WrittenSlots(),

            // The turn index this call has reached, which is already the NEXT one by the time the
            // commit reads it.
            NextTurnIndex = _session.State.TurnIndex,

            Clarifications = _session.Clarifications.Spent(),

            Providers = HarnessSessionState.Capture(_session.AgentSession, _session.Compiled.HarnessStateKeys),

            // Turn-boundary workflow checkpoints on a graph row that reuses its session; every
            // other call leaves this empty and keeps provider state in Providers above.
            WorkflowState = _session.GraphBlob,
        };
    }

    /// <summary>Names the state this call resumes from when store 0 holds none of its own.</summary>
    internal void Resume(CallSessionState stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        // One lock over the check and the write. The guard exists to catch a late hand-off, and a
        // guard that read the session under the lock and then wrote the field outside it would be
        // racing the very reader — OpenSessionAsync at the top of this file — that it guards.
        lock (_session.InterruptLock)
        {
            if (_session.AgentSession is not null)
            {
                throw new InvalidOperationException(
                    $"The call '{_session.CallId}' has already run a turn, and a call resumes only as its "
                    + "first turn opens it. Hand the state to the factory that builds the session "
                    + "instead.");
            }

            _session.Checkpoint = stored;
        }
    }
}


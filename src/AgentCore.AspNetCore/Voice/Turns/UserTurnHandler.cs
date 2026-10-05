// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/agent_activity.py:2379-2436
// (on_start_of_speech/on_end_of_speech, state part only), 2497-2565 (on_interim_transcript/
// on_final_transcript, state part and interrupt), 2633-2709 (on_end_of_turn), 2712-2901
// (_user_turn_completed_task/_impl), commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023
// LiveKit, Inc. Licensed under the Apache License, Version 2.0. Modified: translated to C#; Telnyx owns
// VAD/STT/endpointing, so an interim Utterance stands in for on_start_of_speech's state part and a final
// one for on_end_of_speech's, min-words/backchannel gates and the on_user_turn_completed hook are out,
// and "is this reply now outdated" is a sequence number rather than a task-identity
// comparison, since a .NET task cannot see its own enclosing Task from inside its own body.

using AgentCore.AspNetCore.Voice.Diagnostics;
using AgentCore.AspNetCore.Voice.Session;
using AgentCore.AspNetCore.Voice.Speech;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Voice.Turns
{
    /// <summary>Turns a caller's interim and final prompts into session state and, on a final one, a reply.</summary>
    /// <param name="session">The session whose states, scheduler and away timer this drives.</param>
    /// <param name="activity">Starts the reply a committed turn answers with.</param>
    internal sealed class UserTurnHandler(VoiceSession session, VoiceActivity activity)
    {
        private readonly ILogger _logger = session.Logger;

        private long _latestSequence;

        /// <summary>An interim prompt arrived: the caller is mid-utterance. Never interrupts anything.</summary>
        public void OnInterimTranscript()
        {
            session.SetUserState(UserState.Speaking);
            session.ClearUserSilence();
        }

        /// <summary>A final prompt arrived: the caller's turn is committed. Interrupts the current speech and
        /// starts a reply, unless scheduling is paused or the current speech disallows interruptions.</summary>
        /// <param name="transcript">What the caller said.</param>
        public void OnFinalTranscript(string transcript)
        {
            long endedAt = session.Time.GetTimestamp();

            // on_final_transcript runs before on_end_of_speech (agent_activity.py:2552, 2433): the speech the
            // caller talked over is interrupted before the silence below can release a step it holds.
            activity.InterruptByFinalTranscript();

            session.SetUserState(UserState.Listening);
            session.MarkUserSilent();
            session.RefreshUserAwayOnFinalTranscript();

            if (session.Scheduler.SchedulingPaused)
            {
                VoiceConversationLog.UserTurnSkippedSchedulingPaused(_logger);
                return;
            }

            Task? previous = session.Scheduler.UserTurnTask;
            long sequence = Interlocked.Increment(ref _latestSequence);
            Task turn = session.Scheduler.CreateSpeechTask(() => RunUserTurnAsync(previous, transcript, endedAt, sequence));
            session.Scheduler.SetUserTurnTask(turn);
        }

        private async Task RunUserTurnAsync(Task? previousTurn, string transcript, long endedAt, long sequence)
        {
            if (previousTurn is not null)
            {
                try
                {
                    await previousTurn.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            IReadOnlyList<SpeechHandle> background = session.Scheduler.InterruptBackgroundSpeeches(force: false);
            await Task.WhenAll(background.Select(static speech => speech.WaitForPlayoutAsync())).ConfigureAwait(false);

            SpeechHandle? current = session.Scheduler.CurrentSpeech;
            if (current is not null)
            {
                if (!current.AllowInterruptions)
                {
                    VoiceConversationLog.UserTurnDroppedUninterruptibleSpeech(_logger);
                    return;
                }

                _ = await current.Interrupt(source: InterruptionSource.UserTurn);
            }

            // The close may have paused scheduling while this turn waited (agent_activity.py:2788-2796).
            if (activity.TryGenerateReply(transcript, userTurnEndedAt: endedAt) is not { } reply)
            {
                VoiceConversationLog.UserTurnSkippedSchedulingPaused(_logger);
                return;
            }

            // A newer final prompt already arrived while this one was still working: this reply would
            // answer a turn the caller has moved past (agent_activity.py:2896-2901).
            if (Interlocked.Read(ref _latestSequence) != sequence)
            {
                _ = await reply.Interrupt(source: InterruptionSource.UserTurn);
            }
        }
    }
}

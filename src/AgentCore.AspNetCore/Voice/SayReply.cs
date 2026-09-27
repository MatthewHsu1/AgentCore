// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/agent_activity.py:3100-3356
// (_tts_task, _tts_task_impl), text-only lines 3144-3158, 3160-3173, 3245-3261, 3274-3278, 3346-3352,
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#; no TTS, no audio
// forwarding, no chat-context write (G4), no metrics (2.5: say/filler report none).

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>Speaks fixed text: a <c>say</c>, a filler, or a notice — never a model reply.</summary>
    internal static class SayReply
    {
        /// <summary>Runs one <c>say</c> from authorization to its end.</summary>
        /// <param name="speechHandle">The speech this text belongs to.</param>
        /// <param name="session">Where the agent state this speech drives is read and set.</param>
        /// <param name="output">Where the text goes.</param>
        /// <param name="text">The text, one fragment at a time.</param>
        /// <param name="cancellationToken">The speech's own task token.</param>
        public static async Task RunAsync(
            SpeechHandle speechHandle,
            VoiceSession session,
            IConversationOutputPort output,
            IAsyncEnumerable<string> text,
            CancellationToken cancellationToken)
        {
            List<Task> authorizationTasks = [speechHandle.WaitForAuthorizationAsync(cancellationToken)];
            if (speechHandle.AllowInterruptions)
            {
                authorizationTasks.Add(session.WaitForUserSilenceAsync(cancellationToken));
            }

            await speechHandle.WaitIfNotInterruptedAsync(authorizationTasks).ConfigureAwait(false);
            speechHandle.ClearAuthorization();

            if (speechHandle.IsInterrupted)
            {
                return;
            }

            _ = await TextForwarding.ForwardAsync(
                speechHandle,
                output,
                text,
                onFirstText: () => session.SetAgentState(AgentState.Speaking),
                cancellationToken).ConfigureAwait(false);

            // Only when this speech is still the one that put the session into "speaking": an
            // interruption that landed before the first fragment never made that change.
            if (session.AgentState == AgentState.Speaking)
            {
                session.SetAgentState(session.HasBackgroundSpeeches ? AgentState.Thinking : AgentState.Listening);
            }
        }
    }
}

// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/agent_activity.py:3387-4030
// (_pipeline_reply_task, _pipeline_reply_task_impl), commit d8405f132e1bd960f298190c18daf81ffc1faf45.
// Copyright 2023 LiveKit, Inc. Licensed under the Apache License, Version 2.0. Modified: translated to C#;
// the engine behind IConversationPort runs the model, the tools and every step, so the steps are read off
// its stream; no audio, no TTS, no chat-context writes, no realtime or handoff branch.

using AgentCore.AspNetCore.Voice.Filler;
using AgentCore.AspNetCore.Voice.Session;
using AgentCore.AspNetCore.Voice.Threading;
using AgentCore.AspNetCore.Voice.Turns;

namespace AgentCore.AspNetCore.Voice.Speech.Replies
{
    /// <summary>Speaks one engine turn, one step at a time: the heart of a <c>generate_reply</c>.</summary>
    /// <param name="speechHandle">The speech this reply plays.</param>
    /// <param name="session">The session whose scheduler, output and states the reply drives.</param>
    /// <param name="stream">The engine turn, already reading.</param>
    /// <param name="metrics">Times this reply.</param>
    /// <param name="fillers">The filler each tool id opens while its call runs. Empty by default.</param>
    /// <param name="onFirstText">Runs once, inline on the forwarding task, when this reply hands its first text to the output.</param>
    /// <param name="heardTextWait">How long a reply a final prompt cut mid-step waits for the transport's report.</param>
    /// <param name="transportToken">Cancelled once, when the transport goes away and no report can come.</param>
    internal sealed class PipelineReply(
        SpeechHandle speechHandle,
        VoiceSession session,
        EngineReplyStream stream,
        TurnMetrics metrics,
        IReadOnlyDictionary<string, FillerOptions> fillers,
        Action<PipelineReply> onFirstText,
        TimeSpan heardTextWait,
        CancellationToken transportToken)
    {
        /// <summary>What a voice caller hears when a tool waits on an approval the conversation cannot take.</summary>
        internal const string PendingApprovalNotice =
            "That action needs a human approval, which this conversation can't take.";

        /// <summary>Gets the speech this reply plays.</summary>
        public SpeechHandle SpeechHandle => speechHandle;

        /// <summary>Gets what the caller heard of this reply, and the cut its engine turn takes from it.</summary>
        public ReplyHearing Hearing { get; } = new(stream, session.Time, heardTextWait, transportToken);

        /// <summary>Runs every step of the reply until the engine turn ends or the speech is interrupted.</summary>
        public async Task RunAsync()
        {
            CancellationToken cancellationToken = speechHandle.TaskCancellationToken;
            while (true)
            {
                await speechHandle.WaitIfNotInterruptedAsync([speechHandle.WaitForScheduledAsync()]).ConfigureAwait(false);
                if (speechHandle.IsInterrupted)
                {
                    await EndInterruptedAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                session.SetAgentState(AgentState.Thinking);

                List<Task> authorizationTasks = [speechHandle.WaitForAuthorizationAsync(cancellationToken)];
                if (speechHandle.AllowInterruptions)
                {
                    authorizationTasks.Add(session.WaitForUserSilenceAsync(cancellationToken));
                }

                await speechHandle.WaitIfNotInterruptedAsync(authorizationTasks).ConfigureAwait(false);
                speechHandle.ClearAuthorization();
                if (speechHandle.IsInterrupted)
                {
                    await EndInterruptedAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                await ForwardStepAsync(cancellationToken).ConfigureAwait(false);
                bool hasTools = !speechHandle.IsInterrupted
                    && stream.TryPeek(out ReplyEvent next)
                    && next.Kind == ReplyEventKind.ToolCall;

                if (hasTools)
                {
                    session.SetAgentState(AgentState.Thinking);
                }
                else if (session.AgentState == AgentState.Speaking)
                {
                    session.SetAgentState(AgentState.Listening);
                }

                speechHandle.MarkGenerationDone();

                if (speechHandle.IsInterrupted)
                {
                    await EndInterruptedAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                bool roundDone = hasTools && await WaitForToolsAsync(cancellationToken).ConfigureAwait(false);
                if (speechHandle.IsInterrupted)
                {
                    await EndInterruptedAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!roundDone)
                {
                    EndReply();
                    return;
                }

                speechHandle.IncrementNumSteps();
                session.Scheduler.ScheduleSpeech(speechHandle, SpeechPriority.Normal, force: true);
            }
        }

        private async Task ForwardStepAsync(CancellationToken cancellationToken)
        {
            Task<bool> ready = stream.WaitToReadAsync(cancellationToken).AsTask();
            await speechHandle.WaitIfNotInterruptedAsync([ready]).ConfigureAwait(false);

            // LiveKit opens a segment on the first non-empty text only, so a step with no text never flushes.
            if (speechHandle.IsInterrupted
                || !await ready.ConfigureAwait(false)
                || !stream.TryPeek(out ReplyEvent first)
                || first.Kind != ReplyEventKind.Text)
            {
                return;
            }

            TextForwardingResult forwarded = await TextForwarding.ForwardAsync(
                speechHandle,
                session.Output,
                stream.ReadStepTextAsync(cancellationToken),
                OnFirstText,
                cancellationToken).ConfigureAwait(false);

            Hearing.AddStep(forwarded);
            if (forwarded.Playback != TextPlayback.Skipped)
            {
                metrics.MarkSpeechEnded();
            }
        }

        private void OnFirstText()
        {
            Hearing.MarkFirstText();
            onFirstText(this);
            session.SetAgentState(AgentState.Speaking);
            metrics.MarkFirstSpeech();
        }

        private async Task<bool> WaitForToolsAsync(CancellationToken cancellationToken)
        {
            Dictionary<string, ToolFillerScope> scopes = [];
            List<Task> closing = [];

            void OnToolEvent(ReplyEvent toolEvent)
            {
                if (toolEvent is { Kind: ReplyEventKind.ToolCall, ToolName: { } name }
                    && fillers.TryGetValue(name, out FillerOptions? filler))
                {
                    scopes[toolEvent.Value] = new ToolFillerScope(session, speechHandle, filler);
                }
                else if (toolEvent.Kind == ReplyEventKind.ToolResult && scopes.Remove(toolEvent.Value, out ToolFillerScope? scope))
                {
                    // with_filler exits as its own tool returns (events.py:150-153). A filler speech it already
                    // made plays on, as aclose cancels the scheduler only.
                    closing.Add(scope.DisposeAsync().AsTask());
                }
            }

            session.Scheduler.AddBackgroundSpeech(speechHandle);
            using CancellationTokenSource roundCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<bool> round = stream.WaitForRoundAsync(OnToolEvent, roundCancellation.Token);
            try
            {
                await speechHandle.WaitIfNotInterruptedAsync([round]).ConfigureAwait(false);
                return !speechHandle.IsInterrupted && await round.ConfigureAwait(false);
            }
            finally
            {
                // The round reader stops before the scopes close, so it opens none behind this cleanup.
                await TaskTeardown.CancelAndWaitAsync(roundCancellation, [round]).ConfigureAwait(false);
                session.Scheduler.RemoveBackgroundSpeech(speechHandle);
                closing.AddRange(scopes.Values.Select(scope => scope.DisposeAsync().AsTask()));
                await Task.WhenAll(closing).ConfigureAwait(false);
            }
        }

        private void EndReply()
        {
            if (stream.Fault is { } fault)
            {
                // LiveKit's _on_llm_task_done: a genuine engine failure surfaces through the speech's error.
                speechHandle.MarkDone(fault);
                return;
            }

            metrics.EndReply();

            if (stream.Port.LastTurn?.Approvals is { Count: > 0 })
            {
                _ = session.Say(PendingApprovalNotice);
            }
        }

        private async Task EndInterruptedAsync(CancellationToken cancellationToken)
        {
            bool started = Hearing.CutInterrupted();
            await Hearing.RecutToReportAsync(cancellationToken).ConfigureAwait(false);

            // A turn still waiting on the one before it takes the cut when it starts, and nothing here waits for it.
            if (!started)
            {
                return;
            }

            try
            {
                await stream.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The interruption backstop fired first; it marks the speech done itself.
            }
        }
    }
}

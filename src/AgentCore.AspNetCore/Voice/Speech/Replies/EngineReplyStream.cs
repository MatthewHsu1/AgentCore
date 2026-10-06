// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/generation.py (perform_llm_inference,
// _llm_inference_task: the text and function channels one generation feeds),
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#; the engine behind
// IConversationPort runs the model and the tools, so the steps are read off its one stream.

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Turn.Lifecycle;
using AgentCore.AspNetCore.Voice.Diagnostics;
using AgentCore.AspNetCore.Voice.Turns;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Voice.Speech.Replies
{
    /// <summary>Reads one engine turn and splits it into steps at its tool contents.</summary>
    internal sealed class EngineReplyStream
    {
        private readonly Channel<ReplyEvent> _events = Channel.CreateUnbounded<ReplyEvent>();

        private readonly Lock _gate = new();

        private readonly SpeechHandle _speechHandle;

        private readonly TurnMetrics _metrics;

        private readonly ILogger _logger;

        private volatile Exception? _fault;

        private int? _turnIndex;

        private TurnCut? _cut;

        private EngineReplyStream(IConversationPort port, SpeechHandle speechHandle, TurnMetrics metrics, ILogger logger)
        {
            Port = port;
            _speechHandle = speechHandle;
            _metrics = metrics;
            _logger = logger;
        }

        /// <summary>Gets the conversation the turn runs on.</summary>
        public IConversationPort Port { get; }

        /// <summary>Gets the index of the engine turn, or <see langword="null"/> while it waits on the turn before it.</summary>
        public int? TurnIndex
        {
            get
            {
                lock (_gate)
                {
                    return _turnIndex;
                }
            }
        }

        /// <summary>Gets a task that completes once the engine turn has ended, however it ended. It never faults.</summary>
        public Task Completion { get; private set; } = Task.CompletedTask;

        /// <summary>Gets the fault the engine turn ended with, or <see langword="null"/>. Read it after <see cref="Completion"/>.</summary>
        public Exception? Fault => _fault;

        /// <summary>Starts one engine turn once <paramref name="previousRun"/> has ended, and reads it.</summary>
        /// <param name="port">The conversation the turn runs on.</param>
        /// <param name="userInput">What the caller said.</param>
        /// <param name="previousRun">The <see cref="Completion"/> of the engine turn before, which must end first.</param>
        /// <param name="speechHandle">The speech this turn answers. No reading is taken once it is interrupted.</param>
        /// <param name="metrics">Where each step's time to first token goes.</param>
        /// <param name="logger">Where an engine fault is reported.</param>
        /// <param name="cancellationToken">Cancels the engine turn. Never the speech's own token.</param>
        /// <returns>The stream, already reading.</returns>
        public static EngineReplyStream Start(
            IConversationPort port,
            string userInput,
            Task previousRun,
            SpeechHandle speechHandle,
            TurnMetrics metrics,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            EngineReplyStream stream = new(port, speechHandle, metrics, logger);
            stream.Completion = Task.Run(
                () => stream.RunAsync(userInput, previousRun, cancellationToken), CancellationToken.None);
            return stream;
        }

        /// <summary>Cuts this reply's engine turn where the caller stopped hearing it. Only the first cut counts.</summary>
        /// <param name="cut">What the caller heard.</param>
        /// <returns>
        /// <see langword="true"/> when the turn has started, so the engine holds this cut or an earlier one;
        /// <see langword="false"/> when the turn is still waiting on the one before it, and takes the cut the
        /// moment it starts.
        /// </returns>
        public bool Cut(TurnCut cut)
        {
            lock (_gate)
            {
                if (_cut is not null)
                {
                    return _turnIndex is not null;
                }

                _cut = cut;

                // A report of what was heard can land after the next turn started. The engine then refuses the cut,
                // since that turn's model may already have read this reply, and the history stays as it was.
                if (_turnIndex is { } turnIndex && !Port.Cut(turnIndex, cut))
                {
                    VoiceConversationLog.CutRefused(_logger, turnIndex);
                }

                return _turnIndex is not null;
            }
        }

        /// <summary>Replaces the cut this reply's engine turn already took with a later account of what was heard.</summary>
        /// <param name="cut">What the caller heard, as the transport reported it.</param>
        public void Recut(TurnCut cut)
        {
            lock (_gate)
            {
                if (_cut is null)
                {
                    return;
                }

                _cut = cut;
                if (_turnIndex is { } turnIndex && !Port.Recut(turnIndex, cut))
                {
                    VoiceConversationLog.RecutRefused(_logger, turnIndex);
                }
            }
        }

        /// <summary>Waits until an event is ready to read or the turn has ended.</summary>
        /// <param name="cancellationToken">Cancels this wait only.</param>
        /// <returns><see langword="false"/> once the turn has ended and every event was read.</returns>
        public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken)
        {
            return _events.Reader.WaitToReadAsync(cancellationToken);
        }

        /// <summary>Reads the next event without taking it.</summary>
        /// <param name="next">The next event.</param>
        /// <returns><see langword="false"/> when no event is ready.</returns>
        public bool TryPeek(out ReplyEvent next)
        {
            return _events.Reader.TryPeek(out next);
        }

        /// <summary>Yields the current step's text, and stops before the step's first tool call or at the turn's end.</summary>
        /// <param name="cancellationToken">Stops the enumeration.</param>
        public async IAsyncEnumerable<string> ReadStepTextAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (await _events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_events.Reader.TryPeek(out ReplyEvent next) || next.Kind != ReplyEventKind.Text)
                {
                    yield break;
                }

                _ = _events.Reader.TryRead(out _);
                yield return next.Value;
            }
        }

        /// <summary>Reads the tool events of the current round until its last call is answered.</summary>
        /// <param name="onToolEvent">Sees each tool call and tool result of the round, in stream order.</param>
        /// <param name="cancellationToken">Cancels this wait.</param>
        /// <returns><see langword="true"/> when the round closed, <see langword="false"/> when the turn ended first.</returns>
        public async Task<bool> WaitForRoundAsync(Action<ReplyEvent> onToolEvent, CancellationToken cancellationToken)
        {
            while (await _events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_events.Reader.TryRead(out ReplyEvent next))
                {
                    continue;
                }

                if (next.Kind == ReplyEventKind.RoundDone)
                {
                    return true;
                }

                if (next.Kind is ReplyEventKind.ToolCall or ReplyEventKind.ToolResult)
                {
                    onToolEvent(next);
                }
            }

            return false;
        }

        private async Task RunAsync(string userInput, Task previousRun, CancellationToken cancellationToken)
        {
            try
            {
                await previousRun.ConfigureAwait(false);
                await using TurnRun run = await Port
                    .StartTurnAsync(new ChatMessage(ChatRole.User, userInput), origin: null, cancellationToken)
                    .ConfigureAwait(false);

                lock (_gate)
                {
                    _turnIndex = run.TurnIndex;
                    _metrics.TurnIndex = run.TurnIndex;

                    // A speech cut before its turn could start: the turn still runs, so the caller's words are kept.
                    if (_cut is { } pending)
                    {
                        _ = Port.Cut(run.TurnIndex, pending);
                    }
                }

                await PumpAsync(run).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancelled by its owner, which is not a fault, as LiveKit's _on_llm_task_done skips a cancelled task.
            }
            catch (Exception ex)
            {
                _fault = ex;
                VoiceConversationLog.SpeechTaskFaulted(_logger, ex);
            }
            finally
            {
                _ = _events.Writer.TryComplete();
            }
        }

        private async Task PumpAsync(TurnRun run)
        {
            HashSet<string> openCalls = [];
            List<string> heldText = [];
            _metrics.StartStep();
            await foreach (ChatResponseUpdate update in run.Updates.ConfigureAwait(false))
            {
                foreach (AIContent content in update.Contents)
                {
                    Read(content, openCalls, heldText);
                }
            }

            // A call whose tool threw out of the run never gets a result, so its round never closes. The text
            // after it is the fallback line, and the caller must hear it, unless a cut already ended the speech.
            if (heldText.Count > 0 && !_speechHandle.IsInterrupted)
            {
                CloseRound(heldText);
            }
        }

        private void Read(AIContent content, HashSet<string> openCalls, List<string> heldText)
        {
            switch (content)
            {
                case TextContent { Text.Length: > 0 } text when openCalls.Count > 0:
                    // Text the model wrote after a tool call belongs to the step that follows the round.
                    heldText.Add(text.Text);
                    break;

                case TextContent { Text.Length: > 0 } text:
                    MarkFirstContent();
                    Write(ReplyEventKind.Text, text.Text);
                    break;

                case FunctionCallContent call:
                    MarkFirstContent();
                    _ = openCalls.Add(call.CallId);
                    Write(ReplyEventKind.ToolCall, call.CallId, call.Name);
                    break;

                case FunctionResultContent result when openCalls.Remove(result.CallId):
                    Write(ReplyEventKind.ToolResult, result.CallId);
                    if (openCalls.Count == 0)
                    {
                        CloseRound(heldText);
                    }

                    break;

                default:
                    break;
            }
        }

        private void CloseRound(List<string> heldText)
        {
            Write(ReplyEventKind.RoundDone, string.Empty);
            _metrics.StartStep();
            foreach (string held in heldText)
            {
                MarkFirstContent();
                Write(ReplyEventKind.Text, held);
            }

            heldText.Clear();
        }

        private void MarkFirstContent()
        {
            if (!_speechHandle.IsInterrupted)
            {
                _metrics.MarkFirstContent();
            }
        }

        private void Write(ReplyEventKind kind, string value, string? toolName = null)
        {
            _ = _events.Writer.TryWrite(new ReplyEvent(kind, value, toolName));
        }
    }
}

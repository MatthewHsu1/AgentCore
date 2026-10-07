using System.Text;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.AspNetCore.Calls
{
    /// <summary>
    /// The engine turns of a call whose vendor takes turns itself, and the correction rule: a
    /// new ask while an older one has no answer yet replaces it. Asks take their place in the order they arrive.
    /// </summary>
    internal sealed class CallAsks(PhoneCall call)
    {
        /// <summary>How long a new ask waits for the older ask it cancelled to seal. The engine itself would wait 15 s.</summary>
        internal static readonly TimeSpan OlderAskWait = TimeSpan.FromSeconds(5);

        private readonly Lock _gate = new();

        private CallAsk? _open;

        private bool _stopped;

        private string? _anchor;

        private bool _anchored;

        private int _answeredTurn = -1;

        /// <summary>
        /// Gets the turn of the last ask that was answered, or <see langword="null"/> before the first. The vendor's
        /// agent lines after that answer speak it.
        /// </summary>
        internal int? AnsweredTurn => Volatile.Read(ref _answeredTurn) is >= 0 and var turn ? turn : null;

        /// <summary>Runs one turn on the caller's latest words, and returns what the vendor should say.</summary>
        /// <param name="askId">The vendor's id of this ask; it becomes the user message's id.</param>
        /// <param name="callerWords">The caller's request this ask is for.</param>
        /// <param name="before">
        /// What was said since the last ask ahead of <paramref name="callerWords"/> that no turn answered, oldest
        /// first: the caller's words, and the vendor's own replies as <see cref="FrontVoice"/> lines.
        /// </param>
        /// <param name="onToolCall">Told the name of each tool the turn calls, as it is called.</param>
        /// <param name="cancellationToken">The call's token.</param>
        /// <exception cref="CallConversationLostException">
        /// Another call took the conversation after an idle unload; the vendor hangs this call up.
        /// </exception>
        internal async Task<CallAnswer> AskAsync(
            string askId, string callerWords, IReadOnlyList<ChatMessage> before, Func<string, ValueTask> onToolCall, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(askId);
            ArgumentException.ThrowIfNullOrEmpty(callerWords);
            ArgumentNullException.ThrowIfNull(before);
            ArgumentNullException.ThrowIfNull(onToolCall);

            if (call.HasEnded || !call.HasStarted)
            {
                return Fallback();
            }

            CallAsk mine;
            CallAsk? older;
            ConversationTurnOrigin origin;
            lock (_gate)
            {
                if (_stopped)
                {
                    return CallAnswer.Withdrawn;
                }

                older = _open;
                (mine, origin) = Open(call.Session, older, askId, callerWords, before, cancellationToken);
                _open = mine;
            }

            try
            {
                if (older is not null)
                {
                    await ReplaceAsync(older).ConfigureAwait(false);
                }

                ConversationSession session;
                try
                {
                    session = await call.LiveSessionAsync(mine.Token).ConfigureAwait(false);
                }
                catch (Exception fault) when (fault is not CallConversationLostException)
                {
                    return Failed(mine, fault);
                }

                return await RunAsync(session, mine, origin, onToolCall).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_open, mine))
                    {
                        _open = null;
                    }
                }

                mine.Finish();
            }
        }

        /// <summary>Withdraws the open ask and refuses every later one: the call ended.</summary>
        internal async Task StopAsync()
        {
            CallAsk? open;
            lock (_gate)
            {
                _stopped = true;
                open = _open;
            }

            if (open is not null && !await open.StopAsync(OlderAskWait, call.Host.Time).ConfigureAwait(false))
            {
                CallLog.AskLingeredAtEnd(call.Host.Logger, call.CallId, OlderAskWait.TotalSeconds);
            }
        }

        // A stopped or replaced ask stays silent whatever it threw. Any other fault, a timeout's
        // OperationCanceledException too, is answered with the fallback, as FallbackAgent.IsFault rules.
        private CallAnswer Failed(CallAsk mine, Exception fault)
        {
            if (mine.Token.IsCancellationRequested)
            {
                return CallAnswer.Withdrawn;
            }

            CallLog.AskFailed(call.Host.Logger, call.CallId, fault);
            return Fallback();
        }

        // The engine's own fallback for this entry: the entry's fallbackReply, else the document's.
        private CallAnswer Fallback()
        {
            return CallAnswer.FallbackOf(call.Session.Compiled.FallbackReply);
        }

        // Every ask hangs off the last message the caller heard an answer to, so a resend on the same
        // parent withdraws an unanswered ask, whether or not its turn filed a row yet. The first anchor is the last
        // stored message: a null parent would truncate the whole conversation.
        private (CallAsk Ask, ConversationTurnOrigin Origin) Open(
            ConversationSession session, CallAsk? older, string askId, string callerWords, IReadOnlyList<ChatMessage> before, CancellationToken cancellationToken)
        {
            if (!_anchored)
            {
                _anchor = session.Transcript is [.., { MessageId: { } last }] ? last : null;
                _anchored = true;
            }

            string words = older is null ? callerWords : older.Words + " " + callerWords;
            CallAsk ask = new(words, older is null ? before : [.. older.Before, .. before], CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            return (ask, new ConversationTurnOrigin(askId, _anchor) { NamesParent = true });
        }

        private async Task ReplaceAsync(CallAsk older)
        {
            if (!await older.StopAsync(OlderAskWait, call.Host.Time).ConfigureAwait(false))
            {
                CallLog.OlderAskLingered(call.Host.Logger, call.CallId, OlderAskWait.TotalSeconds);
            }
        }

        private async Task<CallAnswer> RunAsync(ConversationSession session, CallAsk mine, ConversationTurnOrigin origin, Func<string, ValueTask> onToolCall)
        {
            StringBuilder reply = new();
            string? lastMessage = null;
            int turnIndex;
            try
            {
                // A tool the withdrawn ask left running finishes once. Its
                // call and result are rows only once it finished, and this turn reads the rows once, as it starts, so it
                // starts after them; else the model sees no result and runs the tool again.
                await session.ToolRuns.WhenCarriedDone().WaitAsync(mine.Token).ConfigureAwait(false);

                await using TurnRun run = await session.Stream.StartTurnAsync(new ChatMessage(ChatRole.User, mine.Words), origin, mine.Before, mine.Token).ConfigureAwait(false);
                turnIndex = run.TurnIndex;
                await foreach (ChatResponseUpdate update in run.Updates.ConfigureAwait(false))
                {
                    foreach (AIContent content in update.Contents)
                    {
                        switch (content)
                        {
                            case TurnCommittedContent committed:
                                lastMessage = committed.ReplyMessageId ?? committed.UserMessageId;
                                break;
                            case FunctionCallContent tool:
                                await onToolCall(tool.Name).ConfigureAwait(false);
                                break;
                            case TextContent text:
                                _ = reply.Append(text.Text);
                                break;
                            default:
                                break;
                        }
                    }
                }
            }
            catch (Exception fault) when (fault is not CallConversationLostException)
            {
                return Failed(mine, fault);
            }

            lock (_gate)
            {
                if (!ReferenceEquals(_open, mine))
                {
                    return CallAnswer.Withdrawn;
                }

                _open = null;
                _anchor = lastMessage ?? _anchor;
                Volatile.Write(ref _answeredTurn, turnIndex);
            }

            return reply.Length == 0 ? Fallback() : CallAnswer.Answered(reply.ToString());
        }
    }
}

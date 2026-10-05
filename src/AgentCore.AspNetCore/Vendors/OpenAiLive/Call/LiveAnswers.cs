using System.Text.Json.Nodes;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// Answers GPT-Live's delegations through the call core. The answers run off the read loop, and the loop must wait
    /// for every one of them before it closes the call, so they are kept here as one set.
    /// </summary>
    internal sealed class LiveAnswers(
        PhoneCall call,
        LiveCallEnd end,
        Func<JsonObject, CancellationToken, ValueTask> send,
        Func<string> nextEventId,
        Func<bool> leavesAfter,
        ILogger logger)
    {
        internal const string NothingNew = "The caller said nothing new since the last answer.";

        private readonly List<Task> _answers = [];

        internal void Start(string delegationId, IReadOnlyList<LiveLine> before, string words, CancellationToken cancellationToken)
        {
            lock (_answers)
            {
                _answers.Add(AnswerAsync(delegationId, before, words, cancellationToken));
            }
        }

        internal Task WhenAnsweredAsync()
        {
            lock (_answers)
            {
                return Task.WhenAll(_answers);
            }
        }

        // AskAsync runs before this method's first await: asks take their place in arrival order. The call
        // never ends from inside the ask (EndAsync would wait up to CallAsks.OlderAskWait on the ask itself), and never
        // from this task: the loop ends it, after its ledger flush.
        private async Task AnswerAsync(string delegationId, IReadOnlyList<LiveLine> before, string words, CancellationToken cancellationToken)
        {
            try
            {
                if (words.Length == 0)
                {
                    await send(OpenAiLiveEvents.Thinking(delegationId, NothingNew, nextEventId()), cancellationToken).ConfigureAwait(false);
                    if (leavesAfter())
                    {
                        end.Now(ConversationEndReason.AgentCompleted, OpenAiLiveCall.AgentEndedCause);
                    }

                    return;
                }

                CallAnswer answer = await call.Asks.AskAsync(
                    delegationId,
                    words,
                    [.. before.Select(line => line.Speaker == Speaker.Caller ? new ChatMessage(ChatRole.User, line.Text) : FrontVoice.Line(line.Text))],
                    tool => send(OpenAiLiveEvents.Thinking(delegationId, $"The backend is running the tool {tool}. No answer yet.", nextEventId()), cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                if (answer.Kind == CallAnswerKind.Withdrawn)
                {
                    return;
                }

                // Read now and not when the delegation came: a tool of this very answer may have ended the conversation
                // or asked for a transfer.
                await SpeakAsync(delegationId, answer.Text, leavesAfter(), cancellationToken).ConfigureAwait(false);
            }
            catch (CallConversationLostException)
            {
                end.Now(ConversationEndReason.Faulted, OpenAiLiveCall.ConversationLostCause);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The host is stopping.
            }
            catch (Exception fault) when (fault is not OperationCanceledException)
            {
                OpenAiLiveLog.AnswerFaulted(logger, call.CallId, fault);
            }
        }

        // A call that leaves after this answer leaves once GPT-Live acked the last piece and then went quiet for
        // EndQuietWait, or AckWait after that piece's send with no ack: the caller hears the whole answer first.
        private async Task SpeakAsync(string delegationId, string text, bool leaveAfter, CancellationToken cancellationToken)
        {
            IReadOnlyList<string> pieces = CommentaryPieces.Split(text);
            if (leaveAfter && pieces.Count == 0)
            {
                end.Now(ConversationEndReason.AgentCompleted, OpenAiLiveCall.AgentEndedCause);
                return;
            }

            for (int index = 0; index < pieces.Count; index++)
            {
                string eventId = nextEventId();
                if (leaveAfter && index == pieces.Count - 1)
                {
                    end.AfterAck(eventId);
                }

                await send(OpenAiLiveEvents.Commentary(delegationId, pieces[index], eventId), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

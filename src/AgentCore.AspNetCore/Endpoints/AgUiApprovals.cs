using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Formatting;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>
    /// Turns a run's <c>resume</c> array into the turn's approval answers, by the AG-UI interrupt rules
    /// (https://docs.ag-ui.com/concepts/interrupts, "Contract rules"):
    /// <list type="bullet">
    /// <item>One resume must answer every open interrupt; a partial one is refused and nothing is held.</item>
    /// <item>An id the conversation does not hold open is refused, unless the stored transcript already holds the same
    /// answer for it: then the resume is a replay and the run ends with nothing done.</item>
    /// <item><c>resolved</c> carries <c>payload.approved</c> and an optional <c>payload.reason</c>, which reaches the model in
    /// the rejection. <c>cancelled</c> is a refusal with <see cref="CancelledReason"/>.</item>
    /// <item>New words beside a resume that cancels everything (assistant-ui's <c>steerAway</c>) run as a plain turn, so
    /// the conversation refuses the open requests as "moved on" itself.</item>
    /// </list>
    /// </summary>
    internal static class AgUiApprovals
    {
        internal const string CancelledReason = "the user dismissed the request without answering.";

        /// <summary>What a resume comes to: the turn's message, a replay with nothing to do, or a refusal.</summary>
        internal sealed record Reading(ChatMessage? Message, bool Replay = false, string? Code = null, string? Error = null);

        /// <summary>
        /// The new user words of a resume run: the last user message with text, when the client's own last message is a
        /// user message. The converted messages cannot tell: the SDK adds the resume to them.
        /// </summary>
        internal static ChatMessage? WordsBeside(RunAgentInput input, IEnumerable<ChatMessage> messages)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(messages);

            return input.Messages.LastOrDefault() is AGUIUserMessage
                ? AgUiAttachments.LastUserInput([.. messages])
                : null;
        }

        internal static async Task<Reading> ReadAsync(
            ConversationSession conversation, IList<AGUIResume> resume, ChatMessage? words, IConversations conversations, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(conversation);
            ArgumentNullException.ThrowIfNull(resume);
            ArgumentNullException.ThrowIfNull(conversations);

            // A resumed conversation has run no turn in this process yet; opening it loads the queue its last turn filed.
            AgentSession session = await conversation.Ledger.OpenSessionAsync(ct).ConfigureAwait(false);
            Dictionary<string, ToolApprovalRequestContent> open = PendingApprovalQueue.Open(session).ToDictionary(r => r.RequestId, StringComparer.Ordinal);

            if (resume.GroupBy(r => r.InterruptId, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
            {
                return new Reading(null, Code: "duplicate_resume", Error: $"the resume answers interrupt '{twice.Key}' more than once.");
            }

            if (resume.FirstOrDefault(r => !open.ContainsKey(r.InterruptId)) is { } unknown)
            {
                return open.Count == 0 && await AnsweredAlikeAsync(conversation.ConversationId, resume, conversations, ct).ConfigureAwait(false)
                    ? new Reading(null, Replay: true)
                    : new Reading(null, Code: "no_pending_approval", Error: $"the conversation queues no approval under id '{unknown.InterruptId}'.");
            }

            if (open.Keys.Where(id => !resume.Any(r => r.InterruptId == id)).ToList() is { Count: > 0 } missing)
            {
                return new Reading(null, Code: "resume_incomplete",
                    Error: $"one resume must answer every open interrupt; it answers {resume.Count} of {open.Count} (missing: {string.Join(", ", missing)}).");
            }

            if (words is not null && resume.All(r => r.Status == ResumeStatus.Cancelled))
            {
                return new Reading(words);
            }

            List<AIContent> answers = [];
            foreach (AGUIResume entry in resume)
            {
                ToolApprovalResponseContent answer = conversation.TryCreateApprovalAnswer(entry.InterruptId, Approved(entry))!
                    .Contents.OfType<ToolApprovalResponseContent>().Single();
                answer.Reason = ReasonOf(entry);
                answers.Add(answer);
            }

            return new Reading(new ChatMessage(ChatRole.User, [.. answers, .. words?.Contents ?? []]));
        }

        internal static bool Approved(AGUIResume resume)
        {
            ArgumentNullException.ThrowIfNull(resume);

            return resume.Status == ResumeStatus.Resolved
                && resume.Payload is { ValueKind: JsonValueKind.Object } payload
                && payload.TryGetProperty("approved", out JsonElement approved)
                && approved.ValueKind == JsonValueKind.True;
        }

        private static string? ReasonOf(AGUIResume resume)
        {
            return resume.Status == ResumeStatus.Cancelled
                ? CancelledReason
                : resume.Payload is { ValueKind: JsonValueKind.Object } payload
                    && payload.TryGetProperty("reason", out JsonElement reason)
                    && reason.ValueKind == JsonValueKind.String
                    && reason.GetString() is { Length: > 0 } text
                        ? text
                        : null;
        }

        /// <summary>True when the stored transcript already answers every entry the same way, so the resume was sent before.</summary>
        private static async Task<bool> AnsweredAlikeAsync(string conversationId, IList<AGUIResume> resume, IConversations conversations, CancellationToken ct)
        {
            if (await conversations.LoadWindowAsync(conversationId, new TranscriptWindow(null, int.MaxValue), ct).ConfigureAwait(false) is not { } stored)
            {
                return false;
            }

            Dictionary<string, bool> answered = stored.Messages
                .SelectMany(r => r.Content.Contents.OfType<ToolApprovalResponseContent>())
                .GroupBy(a => a.RequestId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last().Approved, StringComparer.Ordinal);
            return resume.All(r => answered.TryGetValue(r.InterruptId, out bool approved) && approved == Approved(r));
        }

        /// <summary>A replayed resume: the run starts and finishes with nothing done, so the client sees no error.</summary>
        internal static async Task WriteReplayAsync(HttpContext http, RunAgentInput input)
        {
            ArgumentNullException.ThrowIfNull(http);
            ArgumentNullException.ThrowIfNull(input);

            http.Response.ContentType = SseEventStreamFormatter.ServerSentEventsMediaType;
            await new SseEventStreamFormatter().WriteAsync(Each(
                new RunStartedEvent { ThreadId = input.ThreadId, RunId = input.RunId },
                new RunFinishedEvent { ThreadId = input.ThreadId, RunId = input.RunId }), http.Response.Body, CancellationToken.None).ConfigureAwait(false);
        }

        private static async IAsyncEnumerable<BaseEvent> Each(params BaseEvent[] events)
        {
            foreach (BaseEvent evt in events)
            {
                await Task.Yield();
                yield return evt;
            }
        }
    }
}

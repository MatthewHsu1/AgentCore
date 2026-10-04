using System.Text.Json;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Harness
{
    /// <summary>
    /// The framework's pending approval queue, read off a session's state bag. The queue is a JSON
    /// array of <c>{toolCall: {name, arguments, conversationId}, requiresConfirmation, requestId}</c> under
    /// <see cref="PendingStateKey"/> — a MAF-internal shape, not a contract. Every read here is
    /// lenient: an entry that lost its shape is skipped, never thrown, because the turn must end
    /// however the queue looks.
    /// </summary>
    internal static class PendingApprovalQueue
    {
        /// <summary>What the function-invocation layer names its queue in the state bag.</summary>
        internal const string PendingStateKey = "_pendingApprovalRequests";

        /// <summary>What the approval layer names its standing and auto rules in the state bag.</summary>
        internal const string StandingStateKey = "toolApprovalState";

        /// <summary>What AgentCore names the answers it holds until every queued request has one.</summary>
        internal const string HeldStateKey = "agentcoreHeldApprovalAnswers";

        /// <summary>Reads the queued requests off a live session.</summary>
        /// <param name="session">The conversation's session.</param>
        /// <returns>The requests, oldest first. The list may be the bag's own copy, so the caller must not change it.</returns>
        internal static IReadOnlyList<ToolApprovalRequestContent> Requests(AgentSession session)
        {
            return session.StateBag.TryGetValue(PendingStateKey, out List<ToolApprovalRequestContent>? queue, TranscriptJson.Options) && queue is not null
                ? queue
                : [];
        }

        /// <summary>
        /// Reads the queued requests no answer covers yet: none AgentCore holds, and none MAF's approval layer
        /// already collected in its own state, where it keeps the answers to a request it showed one at a time.
        /// </summary>
        /// <param name="session">The conversation's session.</param>
        /// <returns>The open requests, oldest first; empty when nothing waits on the caller.</returns>
        internal static List<ToolApprovalRequestContent> Open(AgentSession session)
        {
            IReadOnlyList<ToolApprovalRequestContent> queued = Requests(session);
            if (queued.Count == 0)
            {
                return [];
            }

            HashSet<string> answered = new(Held(session).Select(static answer => answer.RequestId), StringComparer.Ordinal);
            answered.UnionWith(Collected(session.StateBag.Serialize()));
            return [.. queued.Where(request => !answered.Contains(request.RequestId))];
        }

        /// <summary>Reads the answers AgentCore holds back off a live session.</summary>
        /// <param name="session">The conversation's session.</param>
        /// <returns>The held answers, oldest first.</returns>
        internal static List<ToolApprovalResponseContent> Held(AgentSession session)
        {
            return session.StateBag.TryGetValue(HeldStateKey, out List<ToolApprovalResponseContent>? held, TranscriptJson.Options) && held is not null
                ? [.. held]
                : [];
        }

        /// <summary>Keeps the held answers on a live session, or drops the key when none is left.</summary>
        /// <param name="session">The conversation's session.</param>
        /// <param name="held">The answers to keep.</param>
        internal static void Hold(AgentSession session, List<ToolApprovalResponseContent> held)
        {
            if (held.Count == 0)
            {
                _ = session.StateBag.TryRemoveValue(HeldStateKey);
                return;
            }

            session.StateBag.SetValue(HeldStateKey, held, TranscriptJson.Options);
        }

        /// <summary>Drops withdrawn requests from the queue, with any answer AgentCore holds for them.</summary>
        /// <param name="session">The conversation's session.</param>
        /// <param name="withdrawn">The ids of the requests an edit withdrew.</param>
        internal static void Withdraw(AgentSession session, IReadOnlySet<string> withdrawn)
        {
            if (withdrawn.Count == 0)
            {
                return;
            }

            List<ToolApprovalRequestContent> kept = [.. Requests(session).Where(request => !withdrawn.Contains(request.RequestId))];
            if (kept.Count == 0)
            {
                _ = session.StateBag.TryRemoveValue(PendingStateKey);
            }
            else
            {
                session.StateBag.SetValue(PendingStateKey, kept, TranscriptJson.Options);
            }

            Hold(session, [.. Held(session).Where(answer => !withdrawn.Contains(answer.RequestId))]);
        }

        /// <summary>Reads the queue out of a serialized state bag.</summary>
        /// <param name="bag">What <c>AgentSessionStateBag.Serialize</c> produced.</param>
        /// <returns>The pending approvals, oldest first; empty when the bag carries no queue.</returns>
        internal static IReadOnlyList<PendingApproval> Read(JsonElement bag)
        {
            if (!bag.TryGetProperty(PendingStateKey, out JsonElement queue)
                || queue.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<PendingApproval> pending = [];
            foreach (JsonElement entry in queue.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("requestId", out JsonElement id)
                    || id.ValueKind != JsonValueKind.String
                    || !entry.TryGetProperty("toolCall", out JsonElement conversation)
                    || conversation.ValueKind != JsonValueKind.Object
                    || !conversation.TryGetProperty("name", out JsonElement name)
                    || name.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                using JsonDocument empty = JsonDocument.Parse("{}");
                JsonElement arguments = conversation.TryGetProperty("arguments", out JsonElement args) && args.ValueKind == JsonValueKind.Object
                    ? args.Clone()
                    : empty.RootElement.Clone();

                pending.Add(new PendingApproval(
                    id.GetString()!,
                    name.GetString()!,
                    arguments));
            }

            return pending;
        }

        /// <summary>Builds the answer message for one pending request.</summary>
        /// <param name="bag">What <c>AgentSessionStateBag.Serialize</c> produced.</param>
        /// <param name="requestId">The id the caller answers.</param>
        /// <param name="approved">Whether the tool may run.</param>
        /// <returns>
        /// The user message carrying the approval response, or <see langword="null"/> when no queued request carries that id
        /// or its answer is already held.
        /// </returns>
        internal static ChatMessage? AnswerFor(JsonElement bag, string requestId, bool approved)
        {
            if (!bag.TryGetProperty(PendingStateKey, out JsonElement queue)
                || queue.ValueKind != JsonValueKind.Array
                || IsHeld(bag, requestId))
            {
                return null;
            }

            foreach (JsonElement entry in queue.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("requestId", out JsonElement id)
                    || id.GetString() != requestId
                    || !entry.TryGetProperty("toolCall", out JsonElement conversation)
                    || conversation.ValueKind != JsonValueKind.Object
                    || !conversation.TryGetProperty("name", out JsonElement name)
                    || name.ValueKind != JsonValueKind.String
                    || !conversation.TryGetProperty("callId", out JsonElement conversationId)
                    || conversationId.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                Dictionary<string, object?> arguments = new(StringComparer.Ordinal);
                if (conversation.TryGetProperty("arguments", out JsonElement args) && args.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty argument in args.EnumerateObject())
                    {
                        arguments[argument.Name] = ValueOf(argument.Value);
                    }
                }

                FunctionCallContent toolCall = new(conversationId.GetString()!, name.GetString()!, arguments);
                return new ChatMessage(ChatRole.User, [new ToolApprovalResponseContent(requestId, approved, toolCall)]);
            }

            return null;
        }

        // The approval layer's state is MAF-internal: {collectedApprovalResponses: [{requestId, ...}], ...}.
        private static IEnumerable<string> Collected(JsonElement bag)
        {
            if (!bag.TryGetProperty(StandingStateKey, out JsonElement state)
                || state.ValueKind != JsonValueKind.Object
                || !state.TryGetProperty("collectedApprovalResponses", out JsonElement collected)
                || collected.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (JsonElement answer in collected.EnumerateArray())
            {
                if (answer.ValueKind == JsonValueKind.Object
                    && answer.TryGetProperty("requestId", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String)
                {
                    yield return id.GetString()!;
                }
            }
        }

        private static bool IsHeld(JsonElement bag, string requestId)
        {
            return bag.TryGetProperty(HeldStateKey, out JsonElement held)
                && held.ValueKind == JsonValueKind.Array
                && held.EnumerateArray().Any(answer => answer.ValueKind == JsonValueKind.Object
                    && answer.TryGetProperty("requestId", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String
                    && id.GetString() == requestId);
        }

        private static object? ValueOf(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Object => element.EnumerateObject()
                    .ToDictionary(property => property.Name, property => ValueOf(property.Value), StringComparer.Ordinal),
                JsonValueKind.Array => element.EnumerateArray().Select(ValueOf).ToList(),
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out long integer) ? integer : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => null,
            };
        }
    }
}

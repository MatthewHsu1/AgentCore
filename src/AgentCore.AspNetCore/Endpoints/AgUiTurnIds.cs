using System.Globalization;
using System.Text.Json;
using AGUI.Abstractions;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Transcript;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>
    /// The id rules of the AG-UI endpoint.
    /// <list type="bullet">
    /// <item>A turn shows as one AG-UI assistant message per text block, named <c>turn-{turnIndex}.{block}</c>. Block 0
    /// is the turn's first text or tool call; a new block starts when text starts again after a tool call. The turn
    /// index is known before the first update (<c>TurnRun.TurnIndex</c>) and every stored row carries it.</item>
    /// <item>A parent named <c>turn-{k}</c> (the history route's id) or <c>turn-{k}.{n}</c> (a live block) names the last
    /// stored row of turn k.</item>
    /// <item>The user's message is stored under the client's own id (<c>ConversationTurnOrigin.MessageId</c>).</item>
    /// <item><c>forwardedProps.agentcore = { message_id, parent_id }</c>. <c>parent_id</c> present (even null) means the
    /// client names where the turn hangs: everything after the parent is withdrawn. A <c>turn-{k}</c> parent names the
    /// last stored row of turn k.</item>
    /// </list>
    /// </summary>
    internal static class AgUiTurnIds
    {
        public const string TurnPrefix = "turn-";

        public static string TurnMessageId(int turnIndex) => TurnPrefix + turnIndex.ToString(CultureInfo.InvariantCulture);

        public static string BlockMessageId(int turnIndex, int block)
            => TurnMessageId(turnIndex) + "." + block.ToString(CultureInfo.InvariantCulture);

        public static int? TurnIndexOf(string? messageId)
        {
            if (messageId is null || !messageId.StartsWith(TurnPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            ReadOnlySpan<char> rest = messageId.AsSpan(TurnPrefix.Length);
            int dot = rest.IndexOf('.');
            if (dot >= 0 && !int.TryParse(rest[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return null;
            }

            return int.TryParse(dot >= 0 ? rest[..dot] : rest, NumberStyles.None, CultureInfo.InvariantCulture, out int index) ? index : null;
        }

        /// <summary>
        /// Names the AG-UI messages of one run. Text and the tool calls after it share <c>turn-{k}.{n}</c>, and
        /// text that starts after a tool call opens <c>turn-{k}.{n+1}</c>, so text after a tool lands below it. The model's
        /// own message ids are not used: a provider may send none, and AGUI.Server then mints one per update.
        /// </summary>
        public sealed class MessageIds(int turnIndex)
        {
            private int _block;
            private bool _toolInBlock;

            public string Current => BlockMessageId(turnIndex, _block);

            public void Stamp(BaseEvent evt)
            {
                switch (evt)
                {
                    case TextMessageStartEvent start:
                        if (_toolInBlock)
                        {
                            _block++;
                            _toolInBlock = false;
                        }

                        start.MessageId = Current;
                        break;
                    case TextMessageContentEvent content: content.MessageId = Current; break;
                    case TextMessageEndEvent end: end.MessageId = Current; break;
                    case ToolCallStartEvent call:
                        call.ParentMessageId = Current;
                        _toolInBlock = true;
                        break;
                    default:
                        break;
                }
            }
        }

        /// <summary>What the client said about where its turn hangs.</summary>
        public sealed record Anchor(string? MessageId, string? ParentId, bool NamesParent);

        public static Anchor Read(RunAgentInput input)
        {
            ArgumentNullException.ThrowIfNull(input);

            string? lastUser = input.Messages.LastOrDefault(m => m is AGUIUserMessage)?.Id;

            if (input.ForwardedProperties is not { ValueKind: JsonValueKind.Object } forwarded
                || !forwarded.TryGetProperty("agentcore", out JsonElement agentcore)
                || agentcore.ValueKind != JsonValueKind.Object)
            {
                return new Anchor(lastUser, null, NamesParent: false);
            }

            string? messageId = agentcore.TryGetProperty("message_id", out JsonElement m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : lastUser;

            bool namesParent = agentcore.TryGetProperty("parent_id", out JsonElement p);

            string? parentId = namesParent && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            return new Anchor(messageId, parentId, namesParent);
        }

        /// <summary>Turns the client's anchor into the origin the turn loop reads, naming the parent by its stored row id.</summary>
        public static async ValueTask<ConversationTurnOrigin> OriginAsync(
            Anchor anchor, string conversationId, IConversations conversations, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(anchor);
            ArgumentException.ThrowIfNullOrEmpty(conversationId);
            ArgumentNullException.ThrowIfNull(conversations);

            string? parent = anchor.ParentId;
            if (anchor.NamesParent && TurnIndexOf(parent) is { } turn)
            {
                StoredConversation? stored = await conversations.LoadWindowAsync(conversationId, new TranscriptWindow(turn + 1, 1), cancellationToken).ConfigureAwait(false);
                parent = stored?.Messages.Where(r => r.TurnIndex == turn).OrderBy(r => r.Ordinal).LastOrDefault()?.MessageId ?? parent;
            }

            return new ConversationTurnOrigin(anchor.MessageId, parent) { NamesParent = anchor.NamesParent };
        }
    }
}

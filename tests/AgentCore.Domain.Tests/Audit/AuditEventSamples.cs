using AgentCore.Domain.Audit;

namespace AgentCore.Domain.Tests.Audit
{
    /// <summary>The events of one conversation that the vocabulary tests validate.</summary>
    internal static class AuditEventSamples
    {
        /// <summary>What the model produced on the turn these facts describe.</summary>
        internal const string Spoken = "Welcome to Sole, how can I help you today?";

        /// <summary>What the caller heard of it before speaking over the rest.</summary>
        internal const string Heard = "Welcome to Sole, how can I";

        internal static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

        internal static AuditEvent Turn(Guid eventId, int turnIndex)
        {
            return new()
            {
                ConversationId = "conversation-1",
                EventId = eventId,
                Kind = AuditEventKind.TurnCompleted,
                OccurredAt = Start,
                TurnIndex = turnIndex,
                Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.ReplyTextSha256] = AuditHash.OfText(Spoken).Value,
                    [AuditPayloadKeys.StageBefore] = "greeting",
                    [AuditPayloadKeys.StageAfter] = "identify",
                },
            };
        }

        internal static AuditEvent Interruption(Guid eventId, Guid amends, int turnIndex)
        {
            return new()
            {
                ConversationId = "conversation-1",
                EventId = eventId,
                Kind = AuditEventKind.ReplyInterrupted,
                OccurredAt = Start.AddMilliseconds(1_820),
                TurnIndex = turnIndex,
                AmendsEventId = amends,
                Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.UtteranceUntilInterruptSha256] = AuditHash.OfText(Heard).Value,
                    [AuditPayloadKeys.DurationUntilInterruptMs] = "1820",
                },
            };
        }

        internal static AuditEvent FlaggedPrompt(Guid eventId, int turnIndex, string categories = "harassment")
        {
            return new()
            {
                ConversationId = "conversation-1",
                EventId = eventId,
                Kind = AuditEventKind.PromptFlagged,
                OccurredAt = Start.AddMilliseconds(2_400),
                TurnIndex = turnIndex,
                Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.ModerationCategories] = categories,
                },
            };
        }

        internal static AuditEvent Ended()
        {
            return new()
            {
                ConversationId = "conversation-1",
                EventId = Guid.CreateVersion7(),
                Kind = AuditEventKind.ConversationEnded,
                OccurredAt = Start.AddMilliseconds(9_000),
                Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.EndReason] = ConversationEndReasons.ToToken(ConversationEndReason.CallerHungUp),
                },
            };
        }
    }
}

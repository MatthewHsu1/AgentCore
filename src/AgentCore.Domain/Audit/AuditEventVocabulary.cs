namespace AgentCore.Domain.Audit;

/// <summary>
/// The rules an audit event must satisfy before a sink accepts it.
/// </summary>
public static class AuditEventVocabulary
{

    /// <summary>Refuses an event the vocabulary does not permit.</summary>
    public static void Validate(AuditEvent auditEvent)
    {
        if (string.IsNullOrEmpty(auditEvent.CallId))
        {
            throw new ArgumentException("An audit event carries a call id.", nameof(auditEvent));
        }

        if (auditEvent.EventId == Guid.Empty)
        {
            throw new ArgumentException("An audit event carries an identity.", nameof(auditEvent));
        }

        // The token lookup refuses a value outside the closed set.
        _ = AuditEventKinds.ToToken(auditEvent.Kind);

        // The chain is ordered by the sequence the STORE assigns, so "earlier" is not a fact this
        // record can check: an id is an identity and never a position. What is checkable here is the
        // one shape that is always wrong, and the store enforces the rest.
        if (auditEvent.AmendsEventId == auditEvent.EventId)
        {
            throw new ArgumentException(
                $"An amendment names another event. Event {auditEvent.EventId} names itself.",
                nameof(auditEvent));
        }

        if (auditEvent.Kind == AuditEventKind.ReplyInterrupted)
        {
            // T23: the chain is append-only, so a barge-in is a second event that references the
            // turn event. An interruption that amends nothing loses the turn it belongs to.
            if (auditEvent.AmendsEventId is null)
            {
                throw new ArgumentException(
                    "A reply.interrupted event amends the turn.completed event of the same turn, so it sets AmendsEventId. See T23.",
                    nameof(auditEvent));
            }

            // Section 11, item 6a: the event proves the text the caller ACTUALLY HEARD. The words
            // are in store 1, where they stay erasable, so what is required here is the digest.
            if (!auditEvent.Payload.TryGetValue(AuditPayloadKeys.UtteranceUntilInterruptSha256, out string? utterance))
            {
                throw new ArgumentException(
                    $"A reply.interrupted event carries '{AuditPayloadKeys.UtteranceUntilInterruptSha256}', the SHA-256 of the text the caller actually heard. See section 11, item 6a.",
                    nameof(auditEvent));
            }

            RequireHash(auditEvent, AuditPayloadKeys.UtteranceUntilInterruptSha256, utterance);
        }

        if (auditEvent.Payload.TryGetValue(AuditPayloadKeys.ReplyTextSha256, out string? replyText))
        {
            // An empty value here is the one thing that cannot be true: every text hashes to 64
            // characters, the empty string included, so an empty hash proves nothing and would leave
            // the row unverifiable against store 1 forever.
            RequireHash(auditEvent, AuditPayloadKeys.ReplyTextSha256, replyText);
        }

        if (auditEvent.Kind == AuditEventKind.PromptFlagged)
        {
            // §9 makes this chain the only long-term record. The kind alone says something flagged
            // the caller, and the categories are the only other fact the event carries, so the fact
            // goes in with the event or it is lost. This is the argument the chain already makes for
            // utteranceUntilInterrupt above. No AmendsEventId rule stands here: the verdict is known
            // BEFORE the model runs, so the event is written before the turn.completed event of the
            // same turn and amends nothing. TurnIndex names the turn.
            if (!auditEvent.Payload.TryGetValue(AuditPayloadKeys.ModerationCategories, out string? categories)
                || !IsCommaSeparatedListWithNoEmptyMember(categories))
            {
                throw new ArgumentException(
                    $"A prompt.flagged event carries '{AuditPayloadKeys.ModerationCategories}', a comma-separated list with no empty member. See section 11, item 11.",
                    nameof(auditEvent));
            }
        }

        if (auditEvent.Kind == AuditEventKind.CallEnded)
        {
            // The reason is countable, so the chain refuses free text here. §9 makes this table the
            // only long-term record, and a report that counts the endings of one year reads the
            // token. Detail belongs under another key. See CallEndReason.
            if (!auditEvent.Payload.TryGetValue(AuditPayloadKeys.EndReason, out string? endReason)
                || !CallEndReasons.TryParse(endReason, out _))
            {
                throw new ArgumentException(
                    $"A call.ended event carries '{AuditPayloadKeys.EndReason}', and the value is one token of the closed set. See CallEndReason.",
                    nameof(auditEvent));
            }
        }

        foreach (KeyValuePair<string, string> entry in auditEvent.Payload)
        {
            if (string.IsNullOrEmpty(entry.Key))
            {
                throw new ArgumentException("An audit payload key is not empty.", nameof(auditEvent));
            }

            if (entry.Value is null)
            {
                throw new ArgumentException(
                    $"The audit payload value of '{entry.Key}' is null. A missing fact is an absent key.",
                    nameof(auditEvent));
            }
        }
    }


    /// <summary>Reads whether a value is a comma-separated list in which no member is empty.</summary>
    private static bool IsCommaSeparatedListWithNoEmptyMember(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (string member in value.Split(','))
        {
            if (member.Length == 0)
            {
                return false;
            }
        }

        return true;
    }


    /// <summary>
    /// Refuses a payload value that is not a SHA-256 digest.
    /// </summary>
    private static void RequireHash(AuditEvent auditEvent, string key, string? value)
    {
        if (!AuditHash.TryParse(value, out _))
        {
            throw new ArgumentException(
                $"The audit payload value of '{key}' is {AuditHash.Length} lowercase hexadecimal characters. This one is '{value}'.",
                nameof(auditEvent));
        }
    }
}

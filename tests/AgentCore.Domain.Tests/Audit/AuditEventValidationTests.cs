using AgentCore.Domain.Audit;
using Xunit;
using static AgentCore.Domain.Tests.Audit.AuditEventSamples;

namespace AgentCore.Domain.Tests.Audit
{
    /// <summary>
    /// The chain refuses an event it may not hold: an amendment that names nothing or itself, a hash that is not one,
    /// a missing identity, or a conversation end with no reason from the closed set.
    /// </summary>
    public sealed class AuditEventValidationTests
    {
        /// <summary>The reason is counted, so the chain refuses free text under it.</summary>
        [Fact]
        public void AConversationEndedEventWithAFreeTextReason_IsRefused()
        {
            AuditEvent free = Ended() with
            {
                Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.EndReason] = "the caller hung up",
                },
            };

            ArgumentException failure = Assert.Throws<ArgumentException>(
                () => AuditEventVocabulary.Validate(free));

            Assert.Contains(AuditPayloadKeys.EndReason, failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AConversationEndedEventWithoutAReason_IsRefused()
        {
            AuditEvent silent = Ended() with
            {
                Payload = new Dictionary<string, string>(StringComparer.Ordinal),
            };

            _ = Assert.Throws<ArgumentException>(() => AuditEventVocabulary.Validate(silent));
        }

        /// <summary>
        /// T23: the table is append-only, so a barge-in is a second event that references the first.
        /// </summary>
        [Fact]
        public void AnAmendment_ReferencesTheEventItAmends()
        {
            AuditEvent turn = Turn(eventId: Guid.CreateVersion7(), turnIndex: 2);
            AuditEvent interruption = Interruption(eventId: Guid.CreateVersion7(), amends: turn.EventId, turnIndex: 2);

            AuditEvent[] run = [turn, interruption];

            Assert.All(run, AuditEventVocabulary.Validate);
            Assert.Equal(turn.EventId, interruption.AmendsEventId);
            Assert.Equal(turn.ConversationId, interruption.ConversationId);
            Assert.Equal(turn.TurnIndex, interruption.TurnIndex);

            // The first event is untouched. Nothing rewrote the turn, and both events stand.
            Assert.Equal(AuditHash.OfText(Spoken).Value, turn.Payload[AuditPayloadKeys.ReplyTextSha256]);
            Assert.Null(turn.AmendsEventId);
        }

        /// <summary>Section 11, item 6a: the event records the text the caller ACTUALLY HEARD.</summary>
        [Fact]
        public void AnInterruption_RecordsWhatTheCallerHeardAndNotWhatTheModelProduced()
        {
            AuditEvent turn = Turn(eventId: Guid.CreateVersion7(), turnIndex: 0);
            AuditEvent interruption = Interruption(eventId: Guid.CreateVersion7(), amends: turn.EventId, turnIndex: 0);

            string produced = turn.Payload[AuditPayloadKeys.ReplyTextSha256];
            string heard = interruption.Payload[AuditPayloadKeys.UtteranceUntilInterruptSha256];

            // The chain holds proof of the words and never the words, so the reviewer's check is against
            // the message store: the amendment proves what the caller heard, and it is not the whole reply.
            Assert.NotEqual(produced, heard);
            Assert.Equal(AuditHash.OfText(Heard).Value, heard);
            Assert.Equal(AuditHash.OfText(Spoken).Value, produced);
            Assert.Equal("1820", interruption.Payload[AuditPayloadKeys.DurationUntilInterruptMs]);
        }

        [Fact]
        public void AnInterruptionThatAmendsNothing_IsRefused()
        {
            AuditEvent orphan = Interruption(eventId: Guid.CreateVersion7(), amends: Guid.CreateVersion7(), turnIndex: 0)
                with
            { AmendsEventId = null };

            ArgumentException failure = Assert.Throws<ArgumentException>(
                () => AuditEventVocabulary.Validate(orphan));

            Assert.Contains("T23", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnInterruptionWithoutTheUtterance_IsRefused()
        {
            AuditEvent silent = Interruption(eventId: Guid.CreateVersion7(), amends: Guid.CreateVersion7(), turnIndex: 0) with
            {
                Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.DurationUntilInterruptMs] = "1820",
                },
            };

            ArgumentException failure = Assert.Throws<ArgumentException>(
                () => AuditEventVocabulary.Validate(silent));

            Assert.Contains(AuditPayloadKeys.UtteranceUntilInterruptSha256, failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_EmptyHashOnInterrupt_IsRefused()
        {
            // The chain stores proof of the words and not the words, so the one value that must never
            // reach it is a hash that proves nothing. An empty text still hashes to a full digest.
            AuditEvent unproven = Interruption(eventId: Guid.CreateVersion7(), amends: Guid.CreateVersion7(), turnIndex: 0) with
            {
                Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.UtteranceUntilInterruptSha256] = string.Empty,
                    [AuditPayloadKeys.DurationUntilInterruptMs] = "1820",
                },
            };

            ArgumentException failure = Assert.Throws<ArgumentException>(
                () => AuditEventVocabulary.Validate(unproven));

            Assert.Contains(AuditPayloadKeys.UtteranceUntilInterruptSha256, failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_EmptyHashOnTurnCompleted_IsRefused()
        {
            AuditEvent unproven = Turn(eventId: Guid.CreateVersion7(), turnIndex: 0) with
            {
                Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AuditPayloadKeys.ReplyTextSha256] = string.Empty,
                },
            };

            ArgumentException failure = Assert.Throws<ArgumentException>(
                () => AuditEventVocabulary.Validate(unproven));

            Assert.Contains(AuditPayloadKeys.ReplyTextSha256, failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_RefusesAnEventWithNoIdentity()
        {
            AuditEvent malformed = Turn(eventId: Guid.Empty, turnIndex: 0);

            ArgumentException error = Assert.Throws<ArgumentException>(() => AuditEventVocabulary.Validate(malformed));

            Assert.Contains("identity", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_RefusesAnAmendmentThatNamesItself()
        {
            Guid id = Guid.CreateVersion7();
            AuditEvent circular = Interruption(eventId: id, amends: id, turnIndex: 2);

            ArgumentException error = Assert.Throws<ArgumentException>(() => AuditEventVocabulary.Validate(circular));

            Assert.Contains("names another event", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnEventWithoutAConversationId_IsRefused()
        {
            AuditEvent nameless = Turn(eventId: Guid.CreateVersion7(), turnIndex: 0) with { ConversationId = string.Empty };

            _ = Assert.Throws<ArgumentException>(() => AuditEventVocabulary.Validate(nameless));
        }

        [Fact]
        public void AHash_RefusesEverySpellingButLowercaseHexadecimal()
        {
            // PostgreSQL renders sha256() through encode(..., 'hex'), which is lowercase.
            _ = Assert.Throws<ArgumentException>(() => AuditHash.Parse(new string('A', AuditHash.Length)));
            _ = Assert.Throws<ArgumentException>(() => AuditHash.Parse(new string('0', AuditHash.Length - 1)));
            _ = Assert.Throws<ArgumentException>(() => AuditHash.Parse("not a hash"));
            Assert.False(AuditHash.TryParse(null, out _));
        }
    }
}

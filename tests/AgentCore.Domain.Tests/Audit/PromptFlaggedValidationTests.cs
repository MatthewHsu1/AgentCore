using AgentCore.Domain.Audit;
using Xunit;
using static AgentCore.Domain.Tests.Audit.AuditEventSamples;

namespace AgentCore.Domain.Tests.Audit
{
    /// <summary>
    /// A <c>prompt.flagged</c> event carries the endpoint's categories as it returned them, and amends nothing.
    /// </summary>
    public sealed class PromptFlaggedValidationTests
    {
        /// <summary>
        /// The moderation verdict is known BEFORE the model runs, so the event amends nothing. This is
        /// the rule that differs from <c>reply.interrupted</c>, and it is the reason the two kinds are
        /// two kinds.
        /// </summary>
        [Fact]
        public void AFlaggedPrompt_NeedsNoAmendment()
        {
            AuditEvent flagged = FlaggedPrompt(eventId: Guid.CreateVersion7(), turnIndex: 1);

            Assert.Null(flagged.AmendsEventId);
            Assert.Equal(1, flagged.TurnIndex);

            AuditEvent[] run = [flagged];

            Assert.All(run, AuditEventVocabulary.Validate);
        }

        /// <summary>
        /// The kind alone says something flagged the caller. The categories are the only other fact the
        /// event holds, and §9 makes the chain the only long-term record.
        /// </summary>
        [Fact]
        public void AFlaggedPromptWithoutTheCategories_IsRefused()
        {
            AuditEvent silent = FlaggedPrompt(eventId: Guid.CreateVersion7(), turnIndex: 1) with
            {
                Payload = new Dictionary<string, string>(StringComparer.Ordinal),
            };

            ArgumentException failure = Assert.Throws<ArgumentException>(
                () => AuditEventVocabulary.Validate(silent));

            Assert.Contains(AuditPayloadKeys.ModerationCategories, failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AFlaggedPromptWithAnEmptyCategoryList_IsRefused()
        {
            AuditEvent empty = FlaggedPrompt(eventId: Guid.CreateVersion7(), turnIndex: 1, categories: string.Empty);

            ArgumentException failure = Assert.Throws<ArgumentException>(
                () => AuditEventVocabulary.Validate(empty));

            Assert.Contains(AuditPayloadKeys.ModerationCategories, failure.Message, StringComparison.Ordinal);
        }

        /// <summary>A reader splits on a comma and counts, and a blank member makes the count wrong.</summary>
        [Theory]
        [InlineData("harassment,,violence")]
        [InlineData("harassment,")]
        [InlineData(",harassment")]
        public void AFlaggedPromptWithABlankCategory_IsRefused(string categories)
        {
            AuditEvent blank = FlaggedPrompt(eventId: Guid.CreateVersion7(), turnIndex: 1, categories: categories);

            ArgumentException failure = Assert.Throws<ArgumentException>(
                () => AuditEventVocabulary.Validate(blank));

            Assert.Contains(AuditPayloadKeys.ModerationCategories, failure.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The category list is stored exactly as the endpoint returned it. Nothing sorts it, and
        /// nothing rewrites it.
        /// </summary>
        [Fact]
        public void AFlaggedPromptKeepsTheOrderTheEndpointReturned()
        {
            AuditEvent first = FlaggedPrompt(eventId: Guid.CreateVersion7(), turnIndex: 1, categories: "harassment,violence");
            AuditEvent second = FlaggedPrompt(eventId: Guid.CreateVersion7(), turnIndex: 1, categories: "violence,harassment");

            AuditEventVocabulary.Validate(first);
            AuditEventVocabulary.Validate(second);

            Assert.Equal("harassment,violence", first.Payload[AuditPayloadKeys.ModerationCategories]);
            Assert.Equal("violence,harassment", second.Payload[AuditPayloadKeys.ModerationCategories]);
        }

        /// <summary>
        /// The taxonomy belongs to the moderation endpoint and it is open, unlike
        /// <see cref="ConversationEndReason"/>. A closed set would make <see cref="AuditEventVocabulary.Validate"/> throw on a
        /// category OpenAI added, and destroy the record the chain exists to protect.
        /// </summary>
        [Fact]
        public void ACategoryTheLibraryNeverNamed_IsAccepted()
        {
            AuditEvent novel = FlaggedPrompt(
                eventId: Guid.CreateVersion7(),
                turnIndex: 1,
                categories: "illicit/violent,some-category-openai-added-last-tuesday");

            AuditEventVocabulary.Validate(novel);

            Assert.Equal(
                "illicit/violent,some-category-openai-added-last-tuesday",
                novel.Payload[AuditPayloadKeys.ModerationCategories]);
        }

        /// <summary>The rule requires no amendment, and it forbids none either.</summary>
        [Fact]
        public void AFlaggedPrompt_MayStillCarryAnAmendment()
        {
            AuditEvent turn = Turn(eventId: Guid.CreateVersion7(), turnIndex: 1);
            AuditEvent flagged = FlaggedPrompt(eventId: Guid.CreateVersion7(), turnIndex: 1) with { AmendsEventId = turn.EventId };

            AuditEvent[] run = [turn, flagged];

            Assert.All(run, AuditEventVocabulary.Validate);
            Assert.Equal(turn.EventId, flagged.AmendsEventId);
        }
    }
}

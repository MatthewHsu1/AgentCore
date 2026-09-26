using AgentCore.Application.Knowledge;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Diagnostics
{
    /// <summary>
    /// Every line the turn loop writes: extraction, moderation, the transcript, and knowledge retrieval. Three
    /// of them are the "log once" rows of section 8.7. The session owner's own lines — its workspace folder,
    /// and its close and idle-expiry routine — are <see cref="SessionOwnerLog"/>.
    /// </summary>
    internal static partial class Log
    {
        /// <summary>Section 8.7, row two. The extractor returned an invalid object.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="turnIndex">The zero-based index of the turn that just ran.</param>
        /// <param name="reason">Why the extractor produced nothing.</param>
        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Warning,
            Message = "The extractor of conversation {ConversationId} produced nothing for turn {TurnIndex}: {Reason} "
                + "The slots stay unchanged and the conversation continues.")]
        public static partial void ExtractionFailed(ILogger logger, string conversationId, int turnIndex, string reason);

        /// <summary>Section 8.7, row six. A tool failed four times in a row and the run threw.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="turnIndex">The zero-based index of the turn that just ran.</param>
        /// <param name="exception">The fault the fallback layer caught, message and stack trace both.</param>
        [LoggerMessage(
            EventId = 2,
            Level = LogLevel.Error,
            Message = "A tool of conversation {ConversationId} failed four times in turn {TurnIndex}. "
                + "The turn spoke the fallback and the conversation continues.")]
        public static partial void ToolBudgetSpent(ILogger logger, string conversationId, int turnIndex, Exception exception);

        /// <summary>
        /// Section 8.7, row six, the other cause of it: a fault above the fallback layer, so no tool ever ran.
        /// </summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="turnIndex">The zero-based index of the turn that just ran.</param>
        /// <param name="exception">The fault the run threw, message and stack trace both.</param>
        [LoggerMessage(
            EventId = 28,
            Level = LogLevel.Error,
            Message = "The run of conversation {ConversationId} faulted in turn {TurnIndex}. "
                + "The turn spoke the fallback and the conversation continues.")]
        public static partial void TurnRunFaulted(ILogger logger, string conversationId, int turnIndex, Exception exception);

        /// <summary>Section 8.7, last row. The run returned quietly with no text.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="turnIndex">The zero-based index of the turn that just ran.</param>
        [LoggerMessage(
            EventId = 3,
            Level = LogLevel.Warning,
            Message = "Turn {TurnIndex} of conversation {ConversationId} returned an empty reply, so it spoke the fallback. "
                + "The run reached 40 tool rounds, or the model answered nothing.")]
        public static partial void EmptyReply(ILogger logger, string conversationId, int turnIndex);

        /// <summary>Section 8.7, row five. A guard threw at run time, or its rule did not parse.</summary>
        /// <param name="logger">The logger of the composition root.</param>
        /// <param name="guard">The guard name, or the rule text of an inline guard.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 4,
            Level = LogLevel.Warning,
            Message = "The guard {Guard} failed. It is treated as false and the conversation continues.")]
        public static partial void GuardFailed(ILogger logger, string guard, Exception exception);

        /// <summary>An observer of the conversation refused an event, or faulted behind its own enqueue.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="kind">The wire token of the event kind.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 5,
            Level = LogLevel.Error,
            Message = "The audit sink did not accept the {Kind} event of conversation {ConversationId}. "
                + "The turn continues and the chain has a gap.")]
        public static partial void AuditAppendFailed(ILogger logger, string conversationId, string kind, Exception exception);

        /// <summary>The moderation endpoint flagged what the caller said, so the agent refused the turn.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="turnIndex">The zero-based index of the turn.</param>
        /// <param name="categories">The categories the endpoint flagged, comma-separated, in its order.</param>
        [LoggerMessage(
            EventId = 6,
            Level = LogLevel.Warning,
            Message = "Moderation flagged turn {TurnIndex} of conversation {ConversationId} for {Categories}, "
                + "so the agent refused it and spoke the refusal line.")]
        public static partial void PromptRefused(ILogger logger, string conversationId, int turnIndex, string categories);

        /// <summary>The moderation endpoint did not answer, so the turn ran unchecked.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="turnIndex">The zero-based index of the turn.</param>
        /// <param name="reason">What went wrong.</param>
        [LoggerMessage(
            EventId = 7,
            Level = LogLevel.Warning,
            Message = "Moderation did not answer for turn {TurnIndex} of conversation {ConversationId} ({Reason}). "
                + "The turn ran unchecked, because moderation fails open.")]
        public static partial void ModerationUnavailable(ILogger logger, string conversationId, int turnIndex, string reason);

        /// <summary>The audit queue had no room, so the event was dropped.</summary>
        /// <param name="logger">The logger of the queue.</param>
        /// <param name="conversationId">The id of the conversation the dropped event belongs to.</param>
        /// <param name="eventId">The identity of the dropped event.</param>
        [LoggerMessage(
            EventId = 8,
            Level = LogLevel.Error,
            Message = "The audit queue was full, so event {EventId} of conversation {ConversationId} was dropped. "
                + "The conversation continues and the chain has a gap.")]
        public static partial void AuditQueueFull(ILogger logger, string conversationId, Guid eventId);

        /// <summary>A store 1 write was refused, so the turn has no durable record.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="turnIndex">The zero-based index of the turn that was being written.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 9,
            Level = LogLevel.Warning,
            Message = "The transcript store did not accept turn {TurnIndex} of conversation {ConversationId}. "
                + "The conversation continues and the turn has no durable record.")]
        public static partial void TranscriptWriteFailed(ILogger logger, string conversationId, int turnIndex, Exception exception);

        /// <summary>One knowledge retrieval answered, with what it cost and what it returned.</summary>
        /// <param name="logger">The logger of the knowledge provider.</param>
        /// <param name="agent">The id of the agent that asked.</param>
        /// <param name="cardCount">How many cards the store returned, ranked and linked together.</param>
        /// <param name="record">The loggable part of the retrieval, as a structured field.</param>
        [LoggerMessage(
            EventId = 11,
            Level = LogLevel.Debug,
            Message = "The knowledge base answered agent {Agent} with {CardCount} cards. {Record}")]
        public static partial void KnowledgeRetrieved(
            ILogger logger, string agent, int cardCount, KnowledgeAuditRecord.LogView record);

        /// <summary>A19. One knowledge retrieval threw, and this is the only place the cause survives.</summary>
        /// <param name="logger">The logger of the knowledge provider.</param>
        /// <param name="agent">The id of the agent that asked.</param>
        /// <param name="record">The loggable part of the retrieval, as a structured field.</param>
        /// <param name="exception">The cause. This, and not the record, is where the stack trace lives.</param>
        [LoggerMessage(
            EventId = 12,
            Level = LogLevel.Error,
            Message = "The knowledge base did not answer agent {Agent}. The turn was told it is "
                + "unreachable and the conversation continues. {Record}")]
        public static partial void KnowledgeRetrievalFailed(
            ILogger logger, string agent, KnowledgeAuditRecord.LogView record, Exception exception);

        /// <summary>A resumed conversation could not restore part of its stored state, so it went on without it.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation being resumed.</param>
        /// <param name="reason">Which part was dropped, and why it would not go back.</param>
        [LoggerMessage(
            EventId = 13,
            Level = LogLevel.Warning,
            Message = "Conversation {ConversationId} could not restore part of its stored state: {Reason} "
                + "The conversation resumes without that part.")]
        public static partial void StateRestorePartial(ILogger logger, string conversationId, string reason);

        /// <summary>One conversation had its tail withdrawn, because a caller sent an earlier message again.</summary>
        /// <param name="logger">The logger of the history provider.</param>
        /// <param name="conversationId">The conversation that was cut.</param>
        /// <param name="fromOrdinal">The first ordinal withdrawn. It went too.</param>
        /// <param name="turnIndex">The turn the conversation had reached when the cut arrived.</param>
        [LoggerMessage(
            EventId = 14,
            Level = LogLevel.Debug,
            Message = "An edit withdrew conversation {ConversationId} from ordinal {FromOrdinal} onward, "
                + "at turn {TurnIndex}.")]
        public static partial void ConversationTruncated(ILogger logger, string conversationId, int fromOrdinal, int turnIndex);

        /// <summary>
        /// One turn composed its knowledge scope. Every facet logged <see cref="KnowledgeFacetOrigin.Wildcard"/>
        /// is a facet nothing set: this line is the only warning a deployment gets that <c>wildcard.facets</c>
        /// names a key nothing ever sets.
        /// </summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="turnIndex">The zero-based index of the turn.</param>
        /// <param name="origins">Where each facet's value came from.</param>
        [LoggerMessage(
            EventId = 15,
            Level = LogLevel.Debug,
            Message = "Conversation {ConversationId} turn {TurnIndex} composed the knowledge scope {Origins}.")]
        public static partial void KnowledgeScopeComposed(
            ILogger logger,
            string conversationId,
            int turnIndex,
            IReadOnlyDictionary<string, KnowledgeFacetOrigin> origins);

        /// <summary>§8's probe answered: it dropped one facet, re-searched, and found this many candidates.</summary>
        /// <param name="logger">The logger of the knowledge provider.</param>
        /// <param name="agent">The id of the agent that asked.</param>
        /// <param name="facet">The facet the probe dropped.</param>
        /// <param name="candidateCount">How many distinct values the union of the probe's cards named.</param>
        [LoggerMessage(
            EventId = 16,
            Level = LogLevel.Debug,
            Message = "The probe of agent {Agent} dropped facet {Facet} and found {CandidateCount} candidates.")]
        public static partial void KnowledgeProbeRan(ILogger logger, string agent, string facet, int candidateCount);

        /// <summary>
        /// §8 step 4's probe search threw or timed out. The turn was told the knowledge base holds nothing
        /// rather than that it is unreachable, because the main search that already ran is what answers for
        /// reachability; the probe is an extra question on top of it.
        /// </summary>
        /// <param name="logger">The logger of the knowledge provider.</param>
        /// <param name="agent">The id of the agent that asked.</param>
        /// <param name="facet">The facet the probe was trying to drop.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 17,
            Level = LogLevel.Error,
            Message = "The probe of agent {Agent} did not answer for facet {Facet}. The turn was told the "
                + "knowledge base holds nothing and the conversation continues.")]
        public static partial void KnowledgeProbeFailed(ILogger logger, string agent, string facet, Exception exception);

        /// <summary>An agent declared a hosted tool, and its model reports it cannot run it.</summary>
        /// <param name="logger">The logger of the compiler.</param>
        /// <param name="agentId">The id of the agent that declared the tool.</param>
        /// <param name="toolId">The id of the tool that was dropped.</param>
        /// <param name="marker">The marker type, such as <c>HostedWebSearchTool</c>, so the reader knows which hosted capability was missing.</param>
        /// <param name="model">
        /// A phrase naming the model, such as <c>"the model 'reply'"</c> or <c>"this agent's default
        /// model"</c> when the agent names none. Built at the conversation site so this message never renders an
        /// empty pair of quotes.
        /// </param>
        [LoggerMessage(
            EventId = 18,
            Level = LogLevel.Warning,
            Message = "The agent '{AgentId}' declares the tool '{ToolId}' of type '{Marker}', which asks the model provider "
                + "to run it hosted, and {Model} reports it cannot, so the tool was not "
                + "added. Point this agent at a model whose vendor runs it hosted, or remove "
                + "the tool from this agent.")]
        public static partial void HostedToolDropped(ILogger logger, string agentId, string toolId, string marker, string model);

        /// <summary>Store 1 could not be read as a turn opened, so the turn ran on the words already held.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="turnIndex">The zero-based index of the turn that was opening.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 19,
            Level = LogLevel.Warning,
            Message = "The transcript store could not be read as turn {TurnIndex} of conversation {ConversationId} opened. "
                + "The turn runs on the words the session already holds; a message appended outside a "
                + "turn since the last read is not among them.")]
        public static partial void TranscriptResyncFailed(ILogger logger, string conversationId, int turnIndex, Exception exception);

        /// <summary>A workspace file could not be published: the run had no call to own it.</summary>
        /// <param name="logger">The logger of the tool.</param>
        /// <param name="tool">The tool the model called.</param>
        [LoggerMessage(
            EventId = 23,
            Level = LogLevel.Warning,
            Message = "A file published through '{Tool}' was not kept: the run has no conversation id. "
                + "Files are kept only for a run through a ConversationSession, or a background child of one.")]
        public static partial void SandboxFileHasNoOwner(ILogger logger, string tool);

        /// <summary>A workspace file was refused by name or by policy, and the model was told why.</summary>
        /// <param name="logger">The logger of the tool.</param>
        /// <param name="conversationId">The conversation that would have owned the file.</param>
        /// <param name="name">The file's name.</param>
        /// <param name="reason">Why it was refused.</param>
        [LoggerMessage(
            EventId = 24,
            Level = LogLevel.Warning,
            Message = "The file '{Name}' of conversation {ConversationId} was not published: {Reason}.")]
        public static partial void SandboxFileRefused(ILogger logger, string conversationId, string name, string reason);

        /// <summary>A workspace file could not be read or stored, and the model was told so.</summary>
        /// <param name="logger">The logger of the tool.</param>
        /// <param name="conversationId">The conversation that would have owned the file.</param>
        /// <param name="name">The file's name.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 25,
            Level = LogLevel.Warning,
            Message = "The file '{Name}' of conversation {ConversationId} could not be published.")]
        public static partial void SandboxFileCaptureFailed(ILogger logger, string conversationId, string name, Exception exception);

        /// <summary>The compaction strategy threw after a turn committed, so the session keeps the view it had.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="turnIndex">The zero-based index of the turn that committed.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 26,
            Level = LogLevel.Warning,
            Message = "The transcript of conversation {ConversationId} could not be compacted before turn {TurnIndex}. "
                + "The session keeps the view it had and tries again before the next turn.")]
        public static partial void TranscriptCompactionFailed(ILogger logger, string conversationId, int turnIndex, Exception exception);

        [LoggerMessage(
            EventId = 27,
            Level = LogLevel.Warning,
            Message = "The compaction strategy of conversation {ConversationId} produced, before turn {TurnIndex}, a shape one summary row "
                + "cannot stand for: not one new message over the oldest rows with the rest kept in order. The session keeps the view it had.")]
        public static partial void TranscriptCompactionUnsupported(ILogger logger, string conversationId, int turnIndex);

        /// <summary>A turn was refused or dropped, so none of its words were kept.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="turnIndex">The turn the session would have run.</param>
        /// <param name="reason">The <see cref="Domain.Audit.AuditPayloadKeys.RefusedReason"/> token.</param>
        [LoggerMessage(
            EventId = 40,
            Level = LogLevel.Warning,
            Message = "Turn {TurnIndex} of conversation {ConversationId} was refused ({Reason}), so none of its words "
                + "were kept.")]
        public static partial void TurnRefused(ILogger logger, string conversationId, int turnIndex, string reason);

        /// <summary>The store could not put, renew or clear the conversation's busy mark.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 41,
            Level = LogLevel.Warning,
            Message = "The busy mark of conversation {ConversationId} could not be written. The turn runs on; the "
                + "store still refuses a turn another session saved first.")]
        public static partial void BusyMarkFailed(ILogger logger, string conversationId, Exception exception);

        /// <summary>A running turn found its busy mark taken by another session when it came to renew it.</summary>
        /// <param name="logger">The logger of the session.</param>
        /// <param name="conversationId">The id of the conversation.</param>
        [LoggerMessage(
            EventId = 42,
            Level = LogLevel.Warning,
            Message = "The busy mark of conversation {ConversationId} lapsed while a turn still ran, and another "
                + "session took it. The store refuses whichever of the two turns saves second.")]
        public static partial void BusyMarkLost(ILogger logger, string conversationId);

    }
}

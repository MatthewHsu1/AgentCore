using AgentCore.Application.Knowledge;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Diagnostics;

/// <summary>
/// Every line the library writes. Three of them are the "log once" rows of section 8.7.
/// </summary>
internal static partial class Log
{
    /// <summary>Section 8.7, row two. The extractor returned an invalid object.</summary>
    /// <param name="logger">The logger of the session.</param>
    /// <param name="callId">The id of the call.</param>
    /// <param name="turnIndex">The zero-based index of the turn that just ran.</param>
    /// <param name="reason">Why the extractor produced nothing.</param>
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "The extractor of call {CallId} produced nothing for turn {TurnIndex}: {Reason} "
            + "The slots stay unchanged and the call continues.")]
    public static partial void ExtractionFailed(ILogger logger, string callId, int turnIndex, string reason);

    /// <summary>Section 8.7, row six. A tool failed four times in a row and the run threw.</summary>
    /// <param name="logger">The logger of the session.</param>
    /// <param name="callId">The id of the call.</param>
    /// <param name="turnIndex">The zero-based index of the turn that just ran.</param>
    /// <param name="reason">The message of the fault.</param>
    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "A tool of call {CallId} failed four times in turn {TurnIndex}: {Reason} "
            + "The turn spoke the fallback and the call continues.")]
    public static partial void ToolBudgetSpent(ILogger logger, string callId, int turnIndex, string reason);

    /// <summary>Section 8.7, last row. The run returned quietly with no text.</summary>
    /// <param name="logger">The logger of the session.</param>
    /// <param name="callId">The id of the call.</param>
    /// <param name="turnIndex">The zero-based index of the turn that just ran.</param>
    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Turn {TurnIndex} of call {CallId} returned an empty reply, so it spoke the fallback. "
            + "The run reached 40 tool rounds, or the model answered nothing.")]
    public static partial void EmptyReply(ILogger logger, string callId, int turnIndex);

    /// <summary>Section 8.7, row five. A guard threw at run time, or its rule did not parse.</summary>
    /// <param name="logger">The logger of the composition root.</param>
    /// <param name="guard">The guard name, or the rule text of an inline guard.</param>
    /// <param name="exception">The cause.</param>
    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "The guard {Guard} failed. It is treated as false and the call continues.")]
    public static partial void GuardFailed(ILogger logger, string guard, Exception exception);

    /// <summary>An observer of the call refused an event, or faulted behind its own enqueue.</summary>
    /// <param name="logger">The logger of the session.</param>
    /// <param name="callId">The id of the call.</param>
    /// <param name="kind">The wire token of the event kind.</param>
    /// <param name="exception">The cause.</param>
    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Error,
        Message = "The audit sink did not accept the {Kind} event of call {CallId}. "
            + "The turn continues and the chain has a gap.")]
    public static partial void AuditAppendFailed(ILogger logger, string callId, string kind, Exception exception);

    /// <summary>The moderation endpoint flagged what the caller said, so the agent refused the turn.</summary>
    /// <param name="logger">The logger of the session.</param>
    /// <param name="callId">The id of the call.</param>
    /// <param name="turnIndex">The zero-based index of the turn.</param>
    /// <param name="categories">The categories the endpoint flagged, comma-separated, in its order.</param>
    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "Moderation flagged turn {TurnIndex} of call {CallId} for {Categories}, "
            + "so the agent refused it and spoke the refusal line.")]
    public static partial void PromptRefused(ILogger logger, string callId, int turnIndex, string categories);

    /// <summary>The moderation endpoint did not answer, so the turn ran unchecked.</summary>
    /// <param name="logger">The logger of the session.</param>
    /// <param name="callId">The id of the call.</param>
    /// <param name="turnIndex">The zero-based index of the turn.</param>
    /// <param name="reason">What went wrong.</param>
    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Warning,
        Message = "Moderation did not answer for turn {TurnIndex} of call {CallId} ({Reason}). "
            + "The turn ran unchecked, because moderation fails open.")]
    public static partial void ModerationUnavailable(ILogger logger, string callId, int turnIndex, string reason);

    /// <summary>The audit queue had no room, so the event was dropped.</summary>
    /// <param name="logger">The logger of the queue.</param>
    /// <param name="callId">The id of the call the dropped event belongs to.</param>
    /// <param name="eventId">The identity of the dropped event.</param>
    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Error,
        Message = "The audit queue was full, so event {EventId} of call {CallId} was dropped. "
            + "The call continues and the chain has a gap.")]
    public static partial void AuditQueueFull(ILogger logger, string callId, Guid eventId);

    /// <summary>A store 1 write was refused, so the turn has no durable record.</summary>
    /// <param name="logger">The logger of the session.</param>
    /// <param name="callId">The id of the call.</param>
    /// <param name="turnIndex">The zero-based index of the turn that was being written.</param>
    /// <param name="exception">The cause.</param>
    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Warning,
        Message = "The transcript store did not accept turn {TurnIndex} of call {CallId}. "
            + "The call continues and the turn has no durable record.")]
    public static partial void TranscriptWriteFailed(ILogger logger, string callId, int turnIndex, Exception exception);

    /// <summary>A barge-in cut a reply, so the record now holds what the caller heard.</summary>
    /// <param name="logger">The logger of the session.</param>
    /// <param name="callId">The id of the call.</param>
    /// <param name="turnIndex">The zero-based index of the turn whose reply was cut.</param>
    /// <param name="playedMilliseconds">How much of the reply was played, as the vendor reported it.</param>
    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Debug,
        Message = "A barge-in cut the reply of turn {TurnIndex} of call {CallId} "
            + "after {PlayedMilliseconds} ms, so the record holds what the caller heard.")]
    public static partial void ReplyTruncated(ILogger logger, string callId, int turnIndex, double playedMilliseconds);

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
            + "unreachable and the call continues. {Record}")]
    public static partial void KnowledgeRetrievalFailed(
        ILogger logger, string agent, KnowledgeAuditRecord.LogView record, Exception exception);

    /// <summary>A resumed call could not restore part of its stored state, so it went on without it.</summary>
    /// <param name="logger">The logger of the session.</param>
    /// <param name="callId">The id of the call being resumed.</param>
    /// <param name="reason">Which part was dropped, and why it would not go back.</param>
    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Warning,
        Message = "Call {CallId} could not restore part of its stored state: {Reason} "
            + "The call resumes without that part.")]
    public static partial void StateRestorePartial(ILogger logger, string callId, string reason);

    /// <summary>One call had its tail withdrawn, because a caller sent an earlier message again.</summary>
    /// <param name="logger">The logger of the history provider.</param>
    /// <param name="callId">The call that was cut.</param>
    /// <param name="fromOrdinal">The first ordinal withdrawn. It went too.</param>
    /// <param name="turnIndex">The turn the call had reached when the cut arrived.</param>
    [LoggerMessage(
        EventId = 14,
        Level = LogLevel.Debug,
        Message = "An edit withdrew call {CallId} from ordinal {FromOrdinal} onward, "
            + "at turn {TurnIndex}.")]
    public static partial void CallTruncated(ILogger logger, string callId, int fromOrdinal, int turnIndex);

    /// <summary>
    /// One turn composed its knowledge scope. Every facet logged <see cref="KnowledgeFacetOrigin.Wildcard"/>
    /// is a facet nothing set: this line is the only warning a deployment gets that <c>wildcard.facets</c>
    /// names a key nothing ever sets.
    /// </summary>
    /// <param name="logger">The logger of the session.</param>
    /// <param name="callId">The id of the call.</param>
    /// <param name="turnIndex">The zero-based index of the turn.</param>
    /// <param name="origins">Where each facet's value came from.</param>
    [LoggerMessage(
        EventId = 15,
        Level = LogLevel.Debug,
        Message = "Call {CallId} turn {TurnIndex} composed the knowledge scope {Origins}.")]
    public static partial void KnowledgeScopeComposed(
        ILogger logger,
        string callId,
        int turnIndex,
        IReadOnlyDictionary<string, KnowledgeFacetOrigin> origins);
}

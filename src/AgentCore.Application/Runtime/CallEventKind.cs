namespace AgentCore.Application.Runtime;

/// <summary>
/// The closed set of facts one call raises to its observers.
/// </summary>
public enum CallEventKind
{
    /// <summary>A session of the call started. It is the first fact of every session.</summary>
    CallStarted = 0,

    /// <summary>Moderation flagged what the CALLER said, so the agent refused to answer that turn.</summary>
    PromptFlagged = 1,

    /// <summary>The moderation endpoint did not answer in time, or it faulted, so the turn ran unchecked.</summary>
    ModerationUnavailable = 2,

    /// <summary>The moderation endpoint answered, and it flagged nothing.</summary>
    ModerationClean = 3,

    /// <summary>A tool failed its whole retry budget, and the turn spoke the fallback instead.</summary>
    ToolFailed = 4,

    /// <summary>The run returned quietly with no text, so the turn spoke the fallback.</summary>
    EmptyReply = 5,

    /// <summary>The extractor returned an invalid object, so the slots stayed unchanged.</summary>
    ExtractionFailed = 6,

    /// <summary>One turn ran to the end, and the caller heard the whole reply.</summary>
    TurnCompleted = 7,

    /// <summary>The caller spoke over a reply, so the reply stopped early.</summary>
    ReplyInterrupted = 8,

    /// <summary>A caller sent an earlier message again, so the turns after it were withdrawn.</summary>
    TurnSuperseded = 12,

    /// <summary>A store 1 write was refused, so the durable copy of some words was lost.</summary>
    TranscriptWriteFailed = 10,

    /// <summary>The host ended the call. It is the last fact of the session that ended it.</summary>
    CallEnded = 9,

    /// <summary>A resumed call could not restore part of its stored state: the document changed under it.</summary>
    StateRestorePartial = 11,
}

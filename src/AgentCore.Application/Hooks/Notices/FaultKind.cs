namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>A non-fatal fault.</summary>
    public enum FaultKind
    {
        /// <summary>Writing the transcript failed.</summary>
        TranscriptWriteFailed,

        /// <summary>Resyncing the transcript failed.</summary>
        TranscriptResyncFailed,

        /// <summary>Restoring the conversation state succeeded in part.</summary>
        StateRestorePartial,

        /// <summary>Extracting slots from the turn failed.</summary>
        ExtractionFailed,

        /// <summary>The conversation's busy mark lapsed while a turn still ran, and another session took it.</summary>
        BusyMarkLost,

        /// <summary>A hook threw or ran past its deadline.</summary>
        HookFailed,

        /// <summary>Putting or renewing the conversation's busy mark failed in the store.</summary>
        BusyMarkFailed,

        /// <summary>A hook kept denying tool calls past the approval layer's round cap, so the turn ended as a fault instead of asking a person.</summary>
        DenialRoundsExhausted,
    }
}

namespace AgentCore.Application.Calls;

/// <summary>Whether a call still belongs in a caller's list.</summary>
public enum CallStatus
{
    /// <summary>Listed as usual.</summary>
    Regular,

    /// <summary>Kept, but out of the way.</summary>
    Archived,
}

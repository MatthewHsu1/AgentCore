namespace AgentCore.Domain.Sources;

/// <summary>Which of the two shapes a source takes on screen.</summary>
public enum SourceKind
{
    /// <summary>Something with a title and a place inside it, such as a manual and a page.</summary>
    Document = 0,

    /// <summary>Something with a link the caller can open.</summary>
    Url = 1,
}

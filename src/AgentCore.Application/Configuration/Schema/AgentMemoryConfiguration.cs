namespace AgentCore.Application.Configuration.Schema;

/// <summary>The stores an agentic file block may name.</summary>
public enum AgentFileStoreKind
{
    /// <summary>
    /// The host-created per-call directory: <c>AgentCoreOptions.WorkspaceRoot/&lt;callId&gt;/</c>,
    /// created with the call and deleted with it. The one store no process decides for a caller.
    /// </summary>
    Workspace,
}

/// <summary>
/// One agent's <c>memory:</c> block: the <c>file_memory_*</c> tools over a store. The provider
/// keeps its working folder in the session state (§3.5), so a resumed call reads the same files.
/// </summary>
public sealed record AgentMemoryConfiguration
{
    /// <summary>Gets the store the file-memory tools reach.</summary>
    public required AgentFileStoreKind Store { get; init; }
}

/// <summary>
/// One agent's <c>files:</c> block: the <c>file_access_*</c> tools over a store. Today every
/// <c>file_access_*</c> tool is approval-required by default, reads included (§3.6).
/// </summary>
public sealed record AgentFilesConfiguration
{
    /// <summary>Gets the store the file-access tools reach.</summary>
    public required AgentFileStoreKind Store { get; init; }

    /// <summary>Gets whether the write tools are enabled. <c>write: false</c> leaves read, read_lines, ls, grep.</summary>
    public bool Write { get; init; } = true;
}
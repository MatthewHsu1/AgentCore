using AgentCore.Application.Runtime;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime.Harness;

/// <summary>
/// The store behind an agent's <c>files:</c> block. One instance serves every call: each operation
/// resolves the running call's workspace folder off <see cref="TurnAmbients"/> and forwards to a
/// fresh <see cref="FileSystemAgentFileStore"/> rooted there.
/// </summary>
#pragma warning disable MAAI001 // File-store types are evaluation-only in Microsoft.Agents.AI 1.21.0.
internal sealed class CallScopedAgentFileStore : AgentFileStore
{
    private const string NoTurnMessage =
        "A files: tool runs only while a turn runs through a CallSession with a workspace root bound.";

    private static FileSystemAgentFileStore Inner() =>
        new(TurnAmbients.Current?.Workspace ?? throw new InvalidOperationException(NoTurnMessage));

    public override Task WriteAsync(string path, string content, CancellationToken cancellationToken = default) =>
        Inner().WriteAsync(path, content, cancellationToken);

    public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Inner().ReadAsync(path, cancellationToken);

    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        Inner().DeleteAsync(path, cancellationToken);

    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(string directory, CancellationToken cancellationToken = default) =>
        Inner().ListChildrenAsync(directory, cancellationToken);

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Inner().FileExistsAsync(path, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        Inner().CreateDirectoryAsync(path, cancellationToken);
}
#pragma warning restore MAAI001

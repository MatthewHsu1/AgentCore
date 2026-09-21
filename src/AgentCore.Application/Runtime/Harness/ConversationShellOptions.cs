using Microsoft.Agents.AI.Tools.Shell;
using ShellKind = AgentCore.Application.Configuration.Schema.ShellKind;

namespace AgentCore.Application.Runtime.Harness
{
    /// <summary>
    /// What one agent's <c>shell:</c> block resolved to. Built once at compile time and shared by every
    /// conversation of that agent; <see cref="ConversationShells"/> keys one executor per instance of this record.
    /// </summary>
    /// <param name="Kind">Where the shell runs.</param>
    /// <param name="Policy">The command filter, or <see langword="null"/> for none.</param>
    /// <param name="Timeout">How long one command may run, or <see langword="null"/> for the executor default.</param>
    /// <param name="Env">
    /// The environment to add on top of the one the executor kind already supplies, with every
    /// <c>${secret:name}</c> reference already resolved. <see langword="null"/> or empty for none.
    /// </param>
    /// <param name="MaxOutputBytes">The per-stream output cap, or <see langword="null"/> for the executor default.</param>
    /// <param name="Image">The container image. <see cref="ShellKind.Docker"/> only.</param>
    /// <param name="Network">The Docker network mode. <see cref="ShellKind.Docker"/> only.</param>
    /// <param name="MemoryBytes">The container memory limit. <see cref="ShellKind.Docker"/> only.</param>
    internal sealed record ConversationShellOptions(
        ShellKind Kind,
        ShellPolicy? Policy,
        TimeSpan? Timeout,
        IReadOnlyDictionary<string, string>? Env = null,
        int? MaxOutputBytes = null,
        string? Image = null,
        string? Network = null,
        long? MemoryBytes = null);
}

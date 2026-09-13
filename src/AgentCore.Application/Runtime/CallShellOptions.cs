using AgentCore.Application.Configuration.Schema;
using Microsoft.Agents.AI.Tools.Shell;

namespace AgentCore.Application.Runtime;

/// <summary>
/// What one agent's <c>shell:</c> block resolved to. Built once at compile time and shared by every
/// call of that agent; <see cref="CallShells"/> keys one executor per instance of this record.
/// </summary>
/// <param name="Kind">Where the shell runs.</param>
/// <param name="Policy">The command filter, or <see langword="null"/> for none.</param>
/// <param name="Timeout">How long one command may run, or <see langword="null"/> for the executor default.</param>
internal sealed record CallShellOptions(ShellKind Kind, ShellPolicy? Policy, TimeSpan? Timeout);

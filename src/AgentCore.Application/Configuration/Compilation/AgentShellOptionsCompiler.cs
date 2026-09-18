using System.Text.RegularExpressions;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Secrets;
using Microsoft.Agents.AI.Tools.Shell;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>Compiles one agent's <c>shell:</c> block into <see cref="CallShellOptions"/>.</summary>
internal static class AgentShellOptionsCompiler
{
    /// <summary>Builds this agent's shell options from its declared <c>shell:</c> block.</summary>
    /// <param name="item">The agent being compiled.</param>
    /// <param name="shell">The declared block.</param>
    /// <param name="context">The compile-time seams, including the bound workspace root and resolved secrets.</param>
    /// <param name="pointer">This agent's JSON pointer.</param>
    /// <exception cref="ConfigurationLoadException">
    /// The block names no workspace root, a Docker-only key under <c>kind: local</c>, or a policy
    /// pattern that is not a valid regex.
    /// </exception>
    internal static CallShellOptions Build(
        AgentConfiguration item,
        ShellConfiguration shell,
        AgentCompilationContext context,
        string pointer)
    {
        if (context.WorkspaceRoot is null)
        {
            throw ConfigurationCompiler.Fail(
                ConfigurationError.AppendPointer(pointer, "shell"),
                $"the agent '{item.Id}' declares a shell: block and this host bound no workspace "
                + "root, so the shell has no working directory. Call options.UseWorkspace(...) with "
                + "the folder, or remove the shell: block.");
        }

        if (shell.Kind == ShellKind.Local)
        {
            RejectDockerOnlyKey(item, pointer, "image", shell.Image is not null);
            RejectDockerOnlyKey(item, pointer, "network", shell.Network is not null);
            RejectDockerOnlyKey(item, pointer, "memoryMb", shell.MemoryMb is not null);
        }

        var policy = shell.Policy is { } declared ? BuildPolicy(item, declared, pointer) : null;

        var timeout = shell.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;

        var secrets = context.Secrets ?? ResolvedSecrets.Empty;
        var env = shell.Env.Count == 0
            ? null
            : shell.Env.ToDictionary(
                static entry => entry.Key,
                entry => secrets.Format(entry.Value),
                StringComparer.Ordinal);

        var maxOutputBytes = shell.MaxOutputKb is { } maxOutputKb ? maxOutputKb * 1024 : (int?)null;

        var memoryBytes = shell.MemoryMb is { } memoryMb ? (long)memoryMb * 1024 * 1024 : (long?)null;

        return new CallShellOptions(
            shell.Kind,
            policy,
            timeout,
            env,
            maxOutputBytes,
            shell.Image,
            shell.Network,
            memoryBytes);
    }

    /// <summary>Fails compilation when a Docker-only <c>shell:</c> key appears under <c>kind: local</c>.</summary>
    private static void RejectDockerOnlyKey(AgentConfiguration item, string pointer, string key, bool present)
    {
        if (!present)
        {
            return;
        }

        throw ConfigurationCompiler.Fail(
            ConfigurationError.AppendPointer(pointer, "shell"),
            $"the agent '{item.Id}' declares a shell: {key}: and kind: local, but {key}: only applies to "
            + "kind: docker. Remove it, or set kind: docker.");
    }

    private static ShellPolicy BuildPolicy(
        AgentConfiguration item,
        ShellPolicyConfiguration policy,
        string pointer)
    {
        // ShellPolicy treats a supplied-but-empty allow list as deny-all (it denies any command that
        // matches none of the allow patterns, and an empty list matches nothing), so an agent that
        // declares no allow: must reach the executor with allowList: null, not an empty collection.
        var allow = policy.Allow.Count == 0 ? null : policy.Allow;

        try
        {
            return new ShellPolicy(denyList: policy.Deny, allowList: allow);
        }
        catch (ArgumentException exception)
        {
            // ShellPolicy compiles every pattern into a Regex eagerly in its own constructor, so a
            // bad pattern in either list throws here, at compile time, rather than at the model's
            // first command.
            var bad = policy.Deny.Concat(allow ?? [])
                .FirstOrDefault(pattern => !IsValidRegex(pattern));

            throw ConfigurationCompiler.Fail(
                ConfigurationError.AppendPointer(ConfigurationError.AppendPointer(pointer, "shell"), "policy"),
                $"the agent '{item.Id}' declares a shell: policy whose pattern '{bad}' is not a "
                + $"valid regex: {exception.Message}");
        }
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

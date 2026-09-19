using System.Collections.ObjectModel;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Diagnostics;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Runtime.Harness;

/// <summary>
/// The shell executors one conversation has started. One <see cref="ShellExecutor"/> per
/// <see cref="ConversationShellOptions"/> instance the agent declares, created on first ask, plus one
/// probed environment snapshot per options for the model to read before its first command.
/// </summary>
internal sealed class ConversationShells : IAsyncDisposable
{
    private readonly string _workspace;

    private readonly ILogger? _logger;

    private readonly Lock _lock = new();

    private readonly Dictionary<ConversationShellOptions, ShellExecutor> _executors = new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<ConversationShellOptions, Task<ShellEnvironmentSnapshot>> _environments = new(ReferenceEqualityComparer.Instance);

    private bool _disposed;

    /// <summary>Creates the holder for one conversation.</summary>
    /// <param name="workspace">The conversation's workspace folder, the executor's working directory.</param>
    /// <param name="logger">Where a failed dispose is logged, at Warning. May be <see langword="null"/>.</param>
    public ConversationShells(string workspace, ILogger? logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspace);
        _workspace = workspace;
        _logger = logger;
    }

    /// <summary>
    /// Gets this conversation's executor for <paramref name="options"/>, creating it on first ask.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This conversation's shells have already been torn down.</exception>
    public ShellExecutor Get(ConversationShellOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return GetLocked(options);
        }
    }

    private ShellExecutor GetLocked(ConversationShellOptions options)
    {
        if (_executors.TryGetValue(options, out var existing))
        {
            return existing;
        }

        var created = Create(options);
        _executors.Add(options, created);
        return created;
    }

    /// <summary>
    /// Probes this conversation's executor for <paramref name="options"/> once and caches the snapshot
    /// for the rest of the conversation. A faulted or cancelled probe is dropped so the next turn
    /// retries; resume re-probes a fresh environment.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This conversation's shells have already been torn down.</exception>
    public Task<ShellEnvironmentSnapshot> GetEnvironmentAsync(
        ConversationShellOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_environments.TryGetValue(options, out var cached))
            {
                return cached;
            }

            var probe = ProbeAsync(GetLocked(options), cancellationToken);
            _environments.Add(options, probe);
            _ = probe.ContinueWith(
                static (task, state) =>
                {
                    if ((task.IsFaulted || task.IsCanceled) && state is (ConversationShells shells, ConversationShellOptions key))
                    {
                        lock (shells._lock)
                        {
                            if (shells._environments.TryGetValue(key, out var current)
                                && ReferenceEquals(current, task))
                            {
                                shells._environments.Remove(key);
                            }
                        }
                    }
                },
                (this, options),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return probe;
        }
    }

    private static async Task<ShellEnvironmentSnapshot> ProbeAsync(
        ShellExecutor executor, CancellationToken cancellationToken)
    {
        // A throwaway MAF prober per conversation: its single-executor pin is harmless because the
        // instance never leaves this probe. Probing runs commands, so for kind: docker the
        // container starts on the first turn even when the model never runs a command. That
        // is the price of the block: shell: declares intent, and the hints matter most
        // before the first command.
        ShellEnvironmentProvider prober = new(executor);
        return await prober.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The host variables copied into a <see cref="AgentCore.Application.Configuration.Schema.ShellKind.Local"/> executor's clean environment,
    /// on top of whatever the executor already preserves on its own. LANG missing breaks locale-aware
    /// tools (encoding, sorting); TMPDIR missing sends scratch writes to a directory a sandboxed host
    /// may not grant. PATH and HOME are listed too, so this allowlist does not depend on the
    /// executor's own choice of what it preserves.
    /// </summary>
    private static readonly string[] CleanEnvironmentAllowlist = ["PATH", "HOME", "LANG", "TMPDIR"];

    private ShellExecutor Create(ConversationShellOptions options)
    {
        switch (options.Kind)
        {
            case ShellKind.Local:
                LocalShellExecutorOptions localOptions = new()
                {
                    WorkingDirectory = _workspace,
                    Mode = ShellMode.Persistent,
                    ConfineWorkingDirectory = true,
                    AcknowledgeUnsafe = true,
                    CleanEnvironment = true,
                    Environment = BuildLocalEnvironment(options.Env),
                    Policy = options.Policy,
                };
                if (options.Timeout is { } localTimeout)
                {
                    localOptions.Timeout = localTimeout;
                }

                if (options.MaxOutputBytes is { } localMaxOutputBytes)
                {
                    localOptions.MaxOutputBytes = localMaxOutputBytes;
                }

                return new LocalShellExecutor(localOptions);

            case ShellKind.Docker:
                // MountReadonly is false because the workspace is this conversation's own scratch folder,
                // and files: already writes to it. Every other Docker default (network none,
                // read-only root, nobody user, pids limit) stays unless the shell: block overrides it.
                DockerShellExecutorOptions dockerOptions = new()
                {
                    HostWorkdir = _workspace,
                    MountReadonly = false,
                    Environment = options.Env ?? ReadOnlyDictionary<string, string>.Empty,
                    Policy = options.Policy,
                };
                if (options.Timeout is { } dockerTimeout)
                {
                    dockerOptions.Timeout = dockerTimeout;
                }

                if (options.MaxOutputBytes is { } dockerMaxOutputBytes)
                {
                    dockerOptions.MaxOutputBytes = dockerMaxOutputBytes;
                }

                if (options.Image is { } image)
                {
                    dockerOptions.Image = image;
                }

                if (options.Network is { } network)
                {
                    dockerOptions.Network = network;
                }

                if (options.MemoryBytes is { } memoryBytes)
                {
                    dockerOptions.MemoryBytes = memoryBytes;
                }

                return new DockerShellExecutor(dockerOptions);

            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Kind, "Unknown shell kind.");
        }
    }

    /// <summary>
    /// Builds a <see cref="AgentCore.Application.Configuration.Schema.ShellKind.Local"/> executor's environment: the agent's declared
    /// <c>shell.env</c>, then <see cref="CleanEnvironmentAllowlist"/> backfilled from this process's
    /// own environment for whatever <c>shell.env</c> did not already set.
    /// </summary>
    private static Dictionary<string, string?> BuildLocalEnvironment(IReadOnlyDictionary<string, string>? declared)
    {
        Dictionary<string, string?> environment = new(StringComparer.Ordinal);

        if (declared is not null)
        {
            foreach (var (key, value) in declared)
            {
                environment[key] = value;
            }
        }

        foreach (var name in CleanEnvironmentAllowlist)
        {
            if (!environment.ContainsKey(name) && Environment.GetEnvironmentVariable(name) is { } value)
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    /// <summary>
    /// Disposes every executor this conversation started. Idempotent, and never throws out: a failed
    /// dispose is logged at Warning through <see cref="Log.ShellDisposeFailed"/> instead. A second
    /// concurrent caller returns immediately rather than wait for the first dispose to finish — the
    /// executors are only ever taken out and disposed once.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<ShellExecutor> toDispose;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            toDispose = [.. _executors.Values];
            _executors.Clear();
        }

        foreach (var executor in toDispose)
        {
            try
            {
                await executor.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (_logger is not null)
                {
                    Log.ShellDisposeFailed(_logger, _workspace, exception);
                }
            }
        }
    }
}

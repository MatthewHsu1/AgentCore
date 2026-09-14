using AgentCore.Application.Runtime.Turn;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime.Harness;

// The store behind an agent's <c>files:</c> block, bound per invocation: the framework's
// own file-access tools do the work, rooted at the running call's workspace folder. A
// shared store cannot serve every call — the overrides take no session — so this builds
// the framework provider per call around the same options, exactly like the prefetch
// search provider. State keys and post-invocation storage forward to the shared template:
// accumulation lives in the session store, not on any instance.

#pragma warning disable MAAI001 // File-store types are evaluation-only in Microsoft.Agents.AI 1.21.0.

internal sealed class CallFilesProvider : AIContextProvider, IDisposable
{
    private const string NoTurnMessage =
        "A files: tool runs only while a turn runs through a CallSession with a workspace root bound.";

    private readonly FileAccessProvider _template;

    private readonly FileAccessProviderOptions _options;

    public CallFilesProvider(string root, FileAccessProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _template = new FileAccessProvider(new FileSystemAgentFileStore(root), options);
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => _template.StateKeys;

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceType.IsInstanceOfType(this) && serviceKey is null
            ? this
            : _template.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc />
    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var workspace = TurnRegistry.For(context.Session)?.Workspace
            ?? throw new InvalidOperationException(NoTurnMessage);

        // Not using-declared on purpose: the tools this returns run after this method does,
        // and disposing the provider would dispose the store out from under them. The store
        // holds a path, not handles, so ordinary collection reclaims it.
        FileAccessProvider inner = new(new FileSystemAgentFileStore(workspace), _options);

        return await inner.InvokingAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override ValueTask InvokedCoreAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
        => _template.InvokedAsync(context, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _template.Dispose();
}

#pragma warning restore MAAI001

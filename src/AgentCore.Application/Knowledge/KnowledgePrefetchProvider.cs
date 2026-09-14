using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Knowledge;

// The prefetch half of one agent's knowledge block: runs the framework's own search provider
// per invocation, bound to that invocation's turn. A shared provider cannot serve prefetch —
// the framework invokes its search as (query, cancellation) with no turn — so this builds a
// provider per call around the same options, closing over the turn the session filed. State
// keys and post-invocation storage forward to the shared template: accumulation lives in
// the session store, not on any instance, so a fresh provider per call loses nothing.
internal sealed class KnowledgePrefetchProvider(
    AIContextProvider template,
    TextSearchProviderOptions options,
    ILoggerFactory? loggers,
    KnowledgeSearch.Core core)
    : AIContextProvider
{
    private readonly AIContextProvider _template = template
        ?? throw new ArgumentNullException(nameof(template));

    private readonly TextSearchProviderOptions _options = options
        ?? throw new ArgumentNullException(nameof(options));

    private readonly ILoggerFactory? _loggers = loggers;

    private readonly KnowledgeSearch.Core _core = core
        ?? throw new ArgumentNullException(nameof(core));

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
        var turn = TurnRegistry.For(context.Session);

        AIContextProvider inner = new TextSearchProvider(
            async (query, token) =>
                (IEnumerable<TextSearchProvider.TextSearchResult>)await _core(query, null, turn, token).ConfigureAwait(false),
            _options,
            _loggers);

        return await inner.InvokingAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override ValueTask InvokedCoreAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
        => _template.InvokedAsync(context, cancellationToken);
}

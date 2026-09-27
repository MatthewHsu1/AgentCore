using AgentCore.Application.Runtime.Turn;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime.Harness
{
#pragma warning disable MAAI001 // File-store types are evaluation-only in Microsoft.Agents.AI 1.21.0.
    internal sealed class ConversationFilesProvider : AIContextProvider, IDisposable
    {
        private readonly FileAccessProvider _template;

        private readonly FileAccessProviderOptions _options;

        public ConversationFilesProvider(string root, FileAccessProviderOptions options)
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
            if (TurnRegistry.For(context.Session)?.Workspace is not { } workspace)
            {
                return new AIContext();
            }

            FileAccessProvider inner = new(new FileSystemAgentFileStore(workspace), _options);

            return await inner.InvokingAsync(context, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override ValueTask InvokedCoreAsync(
            InvokedContext context, CancellationToken cancellationToken = default)
        {
            return _template.InvokedAsync(context, cancellationToken);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _template.Dispose();
        }
    }

#pragma warning restore MAAI001
}

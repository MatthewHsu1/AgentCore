using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection.Startup
{
    /// <summary>
    /// Warns about a <c>providers.conversation.filler</c> key that names no served tool.
    /// A warning and not a refusal: a provider can add a tool of that name while a turn runs.
    /// </summary>
    internal static class FillerKeyCheck
    {
        /// <summary>Logs one warning for each filler key <paramref name="servedToolIds"/> does not hold.</summary>
        /// <param name="configuration">The loaded document.</param>
        /// <param name="servedToolIds">Every tool id the registry serves, declared and MCP-discovered alike.</param>
        /// <param name="logger">Where the warnings go.</param>
        internal static void Warn(AgentCoreConfiguration configuration, IReadOnlySet<string> servedToolIds, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(servedToolIds);
            ArgumentNullException.ThrowIfNull(logger);

            if (configuration.Providers?.Conversation?.Filler is not { Count: > 0 } filler)
            {
                return;
            }

            foreach (string toolId in filler.Keys.Where(id => !servedToolIds.Contains(id)).Order(StringComparer.Ordinal))
            {
                StartupLog.FillerToolUnknown(
                    logger,
                    ConfigurationError.AppendPointer("/providers/conversation/filler", toolId),
                    toolId);
            }
        }
    }
}

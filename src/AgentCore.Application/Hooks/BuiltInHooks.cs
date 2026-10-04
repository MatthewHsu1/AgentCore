using AgentCore.Application.Hooks.BuiltIn;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Hooks
{
    /// <summary>AgentCore's own hooks, for a host that builds sessions without ASP.NET. They run first.</summary>
    public static class BuiltInHooks
    {
        /// <summary>Creates the built-in hooks in the order they run.</summary>
        /// <param name="auditSink">Where the audit chain is appended.</param>
        /// <param name="logger">Where the built-ins report, such as the "log once" lines and a row the sink refused, or <see langword="null"/> for nowhere.</param>
        /// <returns>The hooks; put them first in <c>AgentCompilationContext.Hooks</c>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="auditSink"/> is <see langword="null"/>.</exception>
        public static IReadOnlyList<AgentHook> Create(IAuditSinkPort auditSink, ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(auditSink);
            return [new TelemetryHook(), new LoggingHook(logger), new AuditHook(auditSink, logger ?? NullLogger.Instance)];
        }
    }
}

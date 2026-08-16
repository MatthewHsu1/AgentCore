using AgentCore.Application.Audit;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// <see cref="CallObservers.Standard"/> assembles the readings of one call, in the order the cost
/// story of section 7 asks for.
/// </summary>
/// <remarks>
/// The order is the contract: the counters of section 8.6 and the rows of section 8.7 are taken
/// above the enqueue, and code this library did not write goes after all three. The list is what
/// <see cref="CallSessionFactory"/> is handed, so this is where the rule is tested rather than
/// inside the factory that no longer knows it.
/// </remarks>
public sealed class CallObserversTests
{
    /// <summary>A host reading that records nothing and only has to be found in the list.</summary>
    private sealed class HostObserver : ICallObserver
    {
        public ValueTask OnCallEventAsync(CallEvent callEvent, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    /// <summary>A sink that accepts everything, so that the audit reading has something to write to.</summary>
    private sealed class AcceptingSink : IAuditSinkPort
    {
        public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    [Fact]
    public void UnboundSinkBuildsNoAuditReading()
    {
        var observers = CallObservers.Standard(auditSink: null, logger: null);

        Assert.Collection(
            observers,
            observer => Assert.IsType<TelemetryCallObserver>(observer),
            observer => Assert.IsType<LoggingCallObserver>(observer));
    }

    [Fact]
    public void BoundSinkPutsTheAuditReadingLast()
    {
        var observers = CallObservers.Standard(new AcceptingSink(), logger: null);

        Assert.Collection(
            observers,
            observer => Assert.IsType<TelemetryCallObserver>(observer),
            observer => Assert.IsType<LoggingCallObserver>(observer),
            observer => Assert.IsType<AuditCallObserver>(observer));
    }

    [Fact]
    public void HostReadingsComeAfterEveryLibraryReading()
    {
        HostObserver host = new();

        var observers = CallObservers.Standard(new AcceptingSink(), logger: null, [host]);

        Assert.Collection(
            observers,
            observer => Assert.IsType<TelemetryCallObserver>(observer),
            observer => Assert.IsType<LoggingCallObserver>(observer),
            observer => Assert.IsType<AuditCallObserver>(observer),
            observer => Assert.Same(host, observer));
    }

    [Fact]
    public void HostReadingsComeAfterTheTwoAnUnboundSinkLeaves()
    {
        HostObserver host = new();

        var observers = CallObservers.Standard(auditSink: null, logger: null, [host]);

        Assert.Collection(
            observers,
            observer => Assert.IsType<TelemetryCallObserver>(observer),
            observer => Assert.IsType<LoggingCallObserver>(observer),
            observer => Assert.Same(host, observer));
    }
}

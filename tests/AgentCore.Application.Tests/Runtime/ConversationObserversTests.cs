using AgentCore.Application.Audit;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Audit;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// <see cref="ConversationObservers.Standard"/> assembles the readings of one conversation, in the order the cost
/// story of section 7 asks for.
/// </summary>
/// <remarks>
/// The order is the contract: the counters of section 8.6 and the rows of section 8.7 are taken
/// above the enqueue, and code this library did not write goes after all three. The list is what
/// <see cref="ConversationSessionFactory"/> is handed, so this is where the rule is tested rather than
/// inside the factory that no longer knows it.
/// </remarks>
public sealed class ConversationObserversTests
{
    /// <summary>A host reading that records nothing and only has to be found in the list.</summary>
    private sealed class HostObserver : IConversationObserver
    {
        public ValueTask OnConversationEventAsync(ConversationEvent conversationEvent, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    /// <summary>A sink that accepts everything, so that the audit reading has something to write to.</summary>
    private sealed class AcceptingSink : IAuditSinkPort
    {
        public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    [Fact]
    public void EveryLibraryReadingIsTaken()
    {
        // The audit reading is no longer conditional on a host having bound something. The
        // composition root resolves providers.audit for every host and falls back to the in-process
        // memory kind, so all three readings of a conversation are always built and the chain of D23 is
        // written whatever the document names.
        var observers = ConversationObservers.Standard(new AcceptingSink(), logger: null);

        Assert.Equal(3, observers.Count);
        Assert.IsType<AuditConversationObserver>(observers[2]);
    }

    [Fact]
    public void NoSinkIsNotAShapeTheListCanHave()
    {
        // There is no "bound nothing" list to build, so asking for one is a caller's mistake rather
        // than a two-observer reading of the conversation.
        Assert.Throws<ArgumentNullException>(
            "auditSink",
            () => ConversationObservers.Standard(auditSink: null!, logger: null));
    }

    [Fact]
    public void BoundSinkPutsTheAuditReadingLast()
    {
        var observers = ConversationObservers.Standard(new AcceptingSink(), logger: null);

        Assert.Collection(
            observers,
            observer => Assert.IsType<TelemetryConversationObserver>(observer),
            observer => Assert.IsType<LoggingConversationObserver>(observer),
            observer => Assert.IsType<AuditConversationObserver>(observer));
    }

    [Fact]
    public void HostReadingsComeAfterEveryLibraryReading()
    {
        HostObserver host = new();

        var observers = ConversationObservers.Standard(new AcceptingSink(), logger: null, [host]);

        Assert.Collection(
            observers,
            observer => Assert.IsType<TelemetryConversationObserver>(observer),
            observer => Assert.IsType<LoggingConversationObserver>(observer),
            observer => Assert.IsType<AuditConversationObserver>(observer),
            observer => Assert.Same(host, observer));
    }
}

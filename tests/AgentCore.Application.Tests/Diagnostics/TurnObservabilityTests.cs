using AgentCore.TestSupport;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Diagnostics.Metrics;
using System.Diagnostics;
using Xunit;

namespace AgentCore.Application.Tests.Diagnostics;

/// <summary>
/// What a turn reports: one span, three metrics, and the three "log once" rows of section 8.7.
/// </summary>
/// <remarks>
/// Every test here runs offline. There is no collector, no exporter, and no network call.
/// </remarks>
public sealed class TurnObservabilityTests
{
    /// <summary>The only attribute keys any metric of this library may carry. See T61.</summary>
    private static readonly string[] PermittedMetricKeys =
    [
        "agentcore.turn.outcome",
        "agentcore.failure.kind",
        "agentcore.audit.kind",

        // Three values: clean, flagged, unavailable. Three series for each replica, against the
        // 10,000 ceiling of item 12. No call id rides on it, so T61 holds.
        "agentcore.moderation.outcome",
    ];

    private const string PolicyYaml =
        """
        apiVersion: agentcore/v1
        name: observed
        state:
          callerSaidGoodbye: { type: boolean, default: false, writer: extractor }
        guards:
          saidGoodbye: { var: callerSaidGoodbye }
        extractor:
          model: { ref: fill }
          when: after_reply
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: greeter, instructions: "greet the caller" }
            - { id: closer,  instructions: "close the call" }
        policy:
          initial: greeting
          stages:
            - { id: greeting, agent: greeter, to: [ { stage: close, when: saidGoodbye } ] }
            - { id: close,    agent: closer,  terminal: true }
        """;

    private const string ToolYaml =
        """
        apiVersion: agentcore/v1
        name: observed-tools
        tools:
          - { id: lookup_order, kind: builtin, uses: orders.read, description: "Look up an order by its id." }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: only, instructions: "I answer everything", tools: [ lookup_order ] }
        """;

    private const string StayingNull = """{ "callerSaidGoodbye": null }""";

    // -------------------------------------------------------------------------------------------
    // T61: the metric attributes, and what may never appear on one.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task NoMetricAttribute_EverCarriesTheCallId()
    {
        // A value nothing else in the process can produce, so a hit is this call and not another.
        var callId = "call-" + Guid.NewGuid().ToString("N");

        var tags = await MeasureAsync(callId);

        // The .NET default is cumulative temporality, so one call id on a metric attribute is one
        // permanent series. A day of calls would then be a day of permanent series, and the free
        // tier binds at 10,000. See D26 and T61.
        Assert.DoesNotContain(tags, tag => string.Equals(tag.Value as string, callId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryMetricAttribute_ComesFromAClosedSet()
    {
        var tags = await MeasureAsync("call-" + Guid.NewGuid().ToString("N"));

        // The listener really saw the instruments of this library.
        Assert.NotEmpty(tags);

        // T61 again, and this is the half that catches the next attribute somebody adds. Every key
        // below takes a handful of values the library writes itself. A key that is not on this list
        // has not been costed, so the build refuses it.
        Assert.All(tags, tag => Assert.Contains(tag.Key, PermittedMetricKeys, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ATurn_RecordsItsDurationOnceWithTheOutcome()
    {
        List<Measurement<double>> durations = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (string.Equals(instrument.Meter.Name, AgentCoreTelemetry.MeterName, StringComparison.Ordinal)
                && string.Equals(instrument.Name, "agentcore.turn.duration", StringComparison.Ordinal))
            {
                active.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            lock (durations)
            {
                durations.Add(new Measurement<double>(value, tags));
            }
        });
        listener.Start();

        using SequencedChatClient reply = new("hello there.");
        using SequencedChatClient fill = new(StayingNull);
        TestTimeProvider clock = new();
        var session = Build(PolicyYaml, reply, fill, timeProvider: clock).Create();

        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
        listener.Dispose();

        // Other tests may run beside this one, so the assertion reads what this turn produced rather
        // than the whole list.
        Assert.Contains(durations, sample => sample.Tags.ToArray().Any(tag =>
            string.Equals(tag.Key, "agentcore.turn.outcome", StringComparison.Ordinal)
            && string.Equals(tag.Value as string, "completed", StringComparison.Ordinal)));
    }

    // -------------------------------------------------------------------------------------------
    // The span. A high-cardinality value belongs here and nowhere else.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task ATurn_IsOneSpanThatCarriesTheCallIdAsAGenAiAttribute()
    {
        var callId = "call-" + Guid.NewGuid().ToString("N");
        var spans = await RecordSpansAsync(callId);

        var span = Assert.Single(spans);

        Assert.Equal(AgentCoreTelemetry.TurnActivityName, span.OperationName);

        // Item 7 asks for gen_ai.* attributes on the trace, and gen_ai.conversation.id is the
        // convention's own name for the conversation a request belongs to. A call is that
        // conversation, and a span attribute costs no series.
        Assert.Equal(callId, span.GetTagItem("gen_ai.conversation.id"));
        Assert.Equal(0, span.GetTagItem("agentcore.turn.index"));
        Assert.Equal("greeting", span.GetTagItem("agentcore.stage.before"));
        Assert.Equal("greeting", span.GetTagItem("agentcore.stage.after"));
        Assert.Equal("completed", span.GetTagItem("agentcore.turn.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
    }

    [Fact]
    public async Task AFailedTurn_MarksItsSpanWithTheReasonOfSectionEightSeven()
    {
        List<Activity> spans = [];
        using var listener = ListenTo(spans);

        using SequencedChatClient reply = new("   ");
        using SequencedChatClient fill = new(StayingNull);
        var session = Build(PolicyYaml, reply, fill).Create("call-" + Guid.NewGuid().ToString("N"));

        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        var span = Assert.Single(Snapshot(spans), item => string.Equals(
            item.GetTagItem("gen_ai.conversation.id") as string, session.CallId, StringComparison.Ordinal));

        Assert.Equal("failed", span.GetTagItem("agentcore.turn.outcome"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(CallSession.EmptyReplyReason, span.StatusDescription);
    }

    [Fact]
    public async Task AStreamedTurn_IsOneSpanToo()
    {
        List<Activity> spans = [];
        using var listener = ListenTo(spans);

        using SequencedChatClient reply = new("hello there.");
        using SequencedChatClient fill = new(StayingNull);
        var session = Build(PolicyYaml, reply, fill).Create("call-" + Guid.NewGuid().ToString("N"));

        await foreach (var update in session.RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken))
        {
            Assert.NotNull(update);
        }

        // The span travels on the turn record and not on Activity.Current, because an async iterator
        // restores the execution context of its caller at every yield.
        var span = Assert.Single(Snapshot(spans), item => string.Equals(
            item.GetTagItem("gen_ai.conversation.id") as string, session.CallId, StringComparison.Ordinal));

        Assert.Equal("completed", span.GetTagItem("agentcore.turn.outcome"));
    }

    // -------------------------------------------------------------------------------------------
    // Section 8.7: three rows say "log once".
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task AFailedExtraction_IsLoggedOnceForTheTurnAndTheCallContinues()
    {
        RecordingLogger logger = new();
        using SequencedChatClient reply = new("hello there.");
        using SequencedChatClient fill = new("I am sorry, I cannot do that.");
        var session = Build(PolicyYaml, reply, fill, logger: logger).Create("call-x");

        var turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        // Row two of section 8.7: leave the slots unchanged, log once for the turn, and continue.
        var line = Assert.Single(logger.Of(1));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("call-x", line.Message, StringComparison.Ordinal);
        Assert.NotNull(turn.ExtractionFailure);
        Assert.Equal("hello there.", turn.ReplyText);
    }

    [Fact]
    public async Task AnEmptyReply_IsLoggedOnce()
    {
        RecordingLogger logger = new();
        using SequencedChatClient reply = new("   ");
        using SequencedChatClient fill = new(StayingNull);
        var session = Build(PolicyYaml, reply, fill, logger: logger).Create("call-x");

        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        var line = Assert.Single(logger.Of(3));
        Assert.Equal(LogLevel.Warning, line.Level);

        // The empty reply is not a tool fault, so the tool row stays silent.
        Assert.Empty(logger.Of(2));
    }

    [Fact]
    public async Task TheFourthConsecutiveToolFailure_IsLoggedOnce()
    {
        RecordingLogger logger = new();
        using LoopingToolCallingChatClient reply = new();
        var session = Build(ToolYaml, reply, null, new ThrowingToolBuilder().Create, logger: logger).Create("call-x");

        await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

        // Row six of section 8.7. The turn spoke the fallback, and the call is still alive.
        //
        // ONE line, and the turn spent four tool calls to get here. Each of those four is a row in
        // the chain of D23, which is where a record of a call belongs; the log gets the turn-level
        // fact alone, so the volume an operator pays Grafana Cloud for did not move.
        var line = Assert.Single(logger.Of(2));
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Contains(ThrowingToolBuilder.Message, line.Message, StringComparison.Ordinal);
        Assert.False(session.IsComplete);

        // The tool row and the empty-reply row are different failures, and only one of them fired.
        Assert.Empty(logger.Of(3));
    }

    [Fact]
    public async Task ASessionWithNoLogger_RunsATurnAndThrowsNothing()
    {
        using SequencedChatClient reply = new("   ");
        using SequencedChatClient fill = new("I am sorry, I cannot do that.");
        var session = Build(PolicyYaml, reply, fill).Create();

        // Two of the three "log once" rows fire in this one turn, and neither has anywhere to write.
        var turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        Assert.Equal(CallSession.FallbackReply, turn.ReplyText);
        Assert.NotNull(turn.ExtractionFailure);
    }

    // -------------------------------------------------------------------------------------------
    // Section 8.7, row five: a guard that throws is logged once for each guard.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public void AGuardThatDoesNotParse_IsLoggedOnceUnderItsName()
    {
        RecordingLogger logger = new();
        var document = ConfigurationLoader.LoadYaml(
            """
            apiVersion: agentcore/v1
            name: broken-guard
            guards:
              impossible: { "no_such_operator": [ 1, 2 ] }
            agents:
              items:
                - { id: only, instructions: "I answer everything" }
            """);

        _ = new GuardEvaluator(document.Guards, logger);

        var line = Assert.Single(logger.Of(4));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("impossible", line.Message, StringComparison.Ordinal);
        Assert.NotNull(line.Exception);
    }

    // -------------------------------------------------------------------------------------------
    // Helpers.
    // -------------------------------------------------------------------------------------------
    /// <summary>Runs three turns of one call and collects every metric attribute the library wrote.</summary>
    /// <param name="callId">The id of the call.</param>
    /// <returns>Every attribute of every measurement, flattened.</returns>
    /// <remarks>
    /// The three turns cover the three outcomes: one answers, one meets a section 8.7 row, and one
    /// ends the call. That reaches every instrument the library owns.
    /// </remarks>
    private static async Task<List<KeyValuePair<string, object?>>> MeasureAsync(string callId)
    {
        List<KeyValuePair<string, object?>> tags = [];
        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, active) =>
        {
            if (string.Equals(instrument.Meter.Name, AgentCoreTelemetry.MeterName, StringComparison.Ordinal))
            {
                active.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<double>((_, _, measured, _) => Collect(tags, measured));
        listener.SetMeasurementEventCallback<long>((_, _, measured, _) => Collect(tags, measured));
        listener.Start();

        using SequencedChatClient reply = new("hello there.", "   ", "goodbye.");
        using SequencedChatClient fill = new(StayingNull, StayingNull, """{ "callerSaidGoodbye": true }""");
        var session = Build(PolicyYaml, reply, fill, auditSink: new Application.Audit.Memory.InMemoryAuditSink())
            .Create(callId);

        var token = TestContext.Current.CancellationToken;
        await session.RunTurnAsync("hi", token);
        await session.RunTurnAsync("still there?", token);
        await session.RunTurnAsync("goodbye", token);

        listener.Dispose();
        return tags;
    }

    /// <summary>Adds the attributes of one measurement to the list.</summary>
    private static void Collect(
        List<KeyValuePair<string, object?>> tags,
        ReadOnlySpan<KeyValuePair<string, object?>> measured)
    {
        var copy = measured.ToArray();
        lock (tags)
        {
            tags.AddRange(copy);
        }
    }

    /// <summary>Runs one turn and collects the spans of this library.</summary>
    private static async Task<List<Activity>> RecordSpansAsync(string callId)
    {
        List<Activity> spans = [];
        using var listener = ListenTo(spans);

        using SequencedChatClient reply = new("hello there.");
        using SequencedChatClient fill = new(StayingNull);
        var session = Build(PolicyYaml, reply, fill).Create(callId);

        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        return [.. Snapshot(spans).Where(span => string.Equals(
            span.GetTagItem("gen_ai.conversation.id") as string, callId, StringComparison.Ordinal))];
    }

    /// <summary>Copies what the listener has collected so far.</summary>
    /// <param name="spans">The list the listener writes into.</param>
    /// <returns>The copy, which no other thread can change.</returns>
    /// <remarks>
    /// An <see cref="ActivityListener"/> subscribes to the source of the whole process, so a span
    /// that another test class produces at the same time lands in this list as well. The listener
    /// writes under the lock, so a reader takes the same lock and asserts against a copy. Reading
    /// the list itself races, and the race reports "Collection was modified".
    /// </remarks>
    private static List<Activity> Snapshot(List<Activity> spans)
    {
        lock (spans)
        {
            return [.. spans];
        }
    }

    /// <summary>Subscribes to the one activity source of this library.</summary>
    private static ActivityListener ListenTo(List<Activity> spans)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = source =>
                string.Equals(source.Name, AgentCoreTelemetry.ActivitySourceName, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                lock (spans)
                {
                    spans.Add(activity);
                }
            },
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static CallSessionFactory Build(
        string yaml,
        IChatClient reply,
        IChatClient? fill,
        Func<ToolConfiguration, AITool?>? tools = null,
        TimeProvider? timeProvider = null,
        IAuditSinkPort? auditSink = null,
        ILogger? logger = null)
    {
        // There is always a sink now: CallObservers.Standard takes a required one, because the
        // composition root resolves providers.audit for every host and falls back to the in-process
        // memory kind. An optional parameter has to be a compile-time constant, so the default is
        // spelled here instead — a fact that does not care where its events land gets a fresh
        // in-memory sink and reads exactly as it did when it passed nothing.
        IAuditSinkPort sink = auditSink ?? new InMemoryAuditSink();

        var document = ConfigurationLoader.LoadYaml(yaml);
        RoutingChatClientFactory chatClients = new(reply);
        if (fill is not null)
        {
            chatClients.Route("fill", fill);
        }

        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients)
            {
                Tools = TestToolRegistry.From(document, tools, TestContext.Current.CancellationToken),
            });

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            CallSessionFactory.CreateExtractor(compiled, chatClients),
            timeProvider,
            logger,
            CallObservers.Standard(sink, logger));
    }
}

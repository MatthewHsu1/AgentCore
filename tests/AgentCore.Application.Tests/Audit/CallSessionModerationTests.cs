using AgentCore.TestSupport;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Diagnostics;
using AgentCore.Application.Tests.Evaluation.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Application.Tests.Audit;

/// <summary>
/// Moderation reads what the caller said before the model runs, and refuses a flagged turn.
/// </summary>
/// <remarks>
/// <para>
/// The owner decided this on 2026-08-13. It departs from section 11 item 11, which asked for the
/// agent's REPLY to be moderated and recorded. Recording a harmful reply protects nobody, because
/// the caller already heard it. Reply moderation is withdrawn, because the model carries its own
/// safety training, so <c>prompt.flagged</c> is the only moderation kind the chain has.
/// </para>
/// <para>
/// The one rule that separates <c>prompt.flagged</c> from <c>reply.interrupted</c> is here: the
/// verdict is known BEFORE the model runs, so the event is written before <c>turn.completed</c> and
/// amends nothing.
/// </para>
/// </remarks>
public sealed class CallSessionModerationTests
{
    private const string PlainYaml =
        """
        apiVersion: agentcore/v1
        name: moderated
        refusalReply: "I am sorry. I cannot help with that request."
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: only, instructions: "I answer everything" }
        """;

    private const string ExtractingYaml =
        """
        apiVersion: agentcore/v1
        name: moderated-extracting
        state:
          callerSaidGoodbye: { type: boolean, default: false, writer: extractor }
        extractor:
          model: { ref: fill }
          when: after_reply
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: only, instructions: "I answer everything" }
        """;

    // -------------------------------------------------------------------------------------------
    // A flagged prompt.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task AFlaggedPrompt_WritesTheFlagBeforeTheTurnEvent()
    {
        InMemoryAuditSink sink = new();
        var session = Build(PlainYaml, new SequencedChatClient("never spoken"), sink: sink,
            moderation: ScriptedModerationEvaluator.Flagging("harassment")).Create("call-1");

        await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

        var events = sink.EventsOf("call-1");
        var flagged = Assert.Single(events, e => e.Kind == AuditEventKind.PromptFlagged);
        var completed = Assert.Single(events, e => e.Kind == AuditEventKind.TurnCompleted);

        // The verdict precedes the model, so the flag precedes the turn event and amends nothing.
        var order = events.ToList();
        Assert.True(order.IndexOf(flagged) < order.IndexOf(completed));
        Assert.Null(flagged.AmendsEventId);
        Assert.Equal(0, flagged.TurnIndex);
    }

    [Fact]
    public async Task AFlaggedPrompt_CarriesTheCategoriesInTheOrderTheEndpointReturnedThem()
    {
        InMemoryAuditSink sink = new();
        var session = Build(PlainYaml, new SequencedChatClient("never spoken"), sink: sink,
            moderation: ScriptedModerationEvaluator.Flagging("violence", "harassment")).Create("call-1");

        await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

        var flagged = Assert.Single(sink.EventsOf("call-1"), e => e.Kind == AuditEventKind.PromptFlagged);
        Assert.Equal("violence,harassment", flagged.Payload[AuditPayloadKeys.ModerationCategories]);
    }

    [Fact]
    public async Task AFlaggedPrompt_NeverReachesTheModel()
    {
        var model = new SequencedChatClient("never spoken");
        var session = Build(PlainYaml, model, moderation: ScriptedModerationEvaluator.Flagging("hate"))
            .Create("call-1");

        await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

        // Nothing was generated, so nothing harmful was ever produced to be recorded.
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task AFlaggedPrompt_SpeaksTheRefusalLineAndNotTheFallback()
    {
        var session = Build(PlainYaml, new SequencedChatClient("never spoken"),
            moderation: ScriptedModerationEvaluator.Flagging("hate")).Create("call-1");

        var result = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

        Assert.Equal("I am sorry. I cannot help with that request.", result.ReplyText);
        Assert.NotEqual(AgentCoreConfiguration.DefaultFallbackReply, result.ReplyText);
    }

    [Fact]
    public async Task AFlaggedPrompt_IsNotAFailure()
    {
        var session = Build(PlainYaml, new SequencedChatClient("never spoken"),
            moderation: ScriptedModerationEvaluator.Flagging("hate")).Create("call-1");

        var result = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

        // A refusal is not a section 8.7 failure. Nothing broke, and the model was never asked.
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task AFlaggedPrompt_RecordsTheRefusalAsTheTextTheCallerHeard()
    {
        InMemoryAuditSink sink = new();
        var session = Build(PlainYaml, new SequencedChatClient("never spoken"), sink: sink,
            moderation: ScriptedModerationEvaluator.Flagging("hate")).Create("call-1");

        await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

        var completed = Assert.Single(sink.EventsOf("call-1"), e => e.Kind == AuditEventKind.TurnCompleted);
        Assert.Equal(
            AuditHash.OfText("I am sorry. I cannot help with that request.").Value,
            completed.Payload[AuditPayloadKeys.ReplyTextSha256]);
    }

    [Fact]
    public async Task AFlaggedPrompt_RunsNoExtractor()
    {
        // The extractor's only input is the words moderation flagged. A slot filled from them would
        // carry the flagged content into the state document and into every later prompt.
        var fill = new SequencedChatClient("""{ "callerSaidGoodbye": true }""");
        var session = Build(ExtractingYaml, new SequencedChatClient("never spoken"), fill: fill,
            moderation: ScriptedModerationEvaluator.Flagging("hate")).Create("call-1");

        await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

        Assert.Equal(0, fill.Calls);
    }

    [Fact]
    public async Task AFlaggedPrompt_IsLoggedOnceAndNeverCarriesTheWordsThatWereFlagged()
    {
        RecordingLogger logger = new();
        var session = Build(PlainYaml, new SequencedChatClient("never spoken"), logger: logger,
            moderation: ScriptedModerationEvaluator.Flagging("harassment")).Create("call-1");

        await session.RunTurnAsync("the words that were flagged", TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Of(6));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("harassment", entry.Message, StringComparison.Ordinal);

        // The flagged words are the content. A log store holds none of the three defences of D23,
        // so the categories travel and the words do not.
        Assert.DoesNotContain("the words that were flagged", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEventsOfAModeratedCall_FormAChainThatVerifies()
    {
        InMemoryAuditSink sink = new();
        var session = Build(PlainYaml, new SequencedChatClient("never spoken"), sink: sink,
            moderation: ScriptedModerationEvaluator.Flagging("harassment")).Create("call-1");

        await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

        var events = sink.EventsOf("call-1");
        Assert.Equal(
            [AuditEventKind.CallStarted, AuditEventKind.PromptFlagged, AuditEventKind.TurnCompleted],
            events.Select(e => e.Kind).ToArray());
        Assert.All(events, AuditEventVocabulary.Validate);
    }

    // -------------------------------------------------------------------------------------------
    // A clean prompt, and the fail-open rule.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task ACleanPrompt_ReachesTheModelAndWritesNoFlag()
    {
        InMemoryAuditSink sink = new();
        var model = new SequencedChatClient("the ordinary reply");
        var session = Build(PlainYaml, model, sink: sink, moderation: ScriptedModerationEvaluator.Clean())
            .Create("call-1");

        var result = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

        Assert.Equal(1, model.Calls);
        Assert.Equal("the ordinary reply", result.ReplyText);

        // The vocabulary holds no reply.cleared. A turn.completed that no flag precedes is the record.
        Assert.DoesNotContain(sink.EventsOf("call-1"), e => e.Kind == AuditEventKind.PromptFlagged);
    }

    [Fact]
    public async Task TheModeratedText_IsWhatTheCallerSaid()
    {
        var endpoint = ScriptedModerationEvaluator.Clean();
        var session = Build(PlainYaml, new SequencedChatClient("hello"), moderation: endpoint).Create("call-1");

        await session.RunTurnAsync("how do I reset the console", TestContext.Current.CancellationToken);

        Assert.Equal(["how do I reset the console"], endpoint.Moderated);
    }

    [Fact]
    public async Task AnEndpointThatDidNotAnswer_LetsTheTurnThrough()
    {
        // Fail open. A vendor outage must not refuse every caller on a support line.
        InMemoryAuditSink sink = new();
        var model = new SequencedChatClient("the ordinary reply");
        var session = Build(PlainYaml, model, sink: sink, moderation: ScriptedModerationEvaluator.Unanswered())
            .Create("call-1");

        var result = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

        Assert.Equal(1, model.Calls);
        Assert.Equal("the ordinary reply", result.ReplyText);
        Assert.DoesNotContain(sink.EventsOf("call-1"), e => e.Kind == AuditEventKind.PromptFlagged);
    }

    [Fact]
    public async Task AnEndpointThatThrows_LetsTheTurnThroughAndLogsOnce()
    {
        RecordingLogger logger = new();
        var model = new SequencedChatClient("the ordinary reply");
        var session = Build(PlainYaml, model, logger: logger,
            moderation: ScriptedModerationEvaluator.Throwing(new InvalidOperationException("boom")))
            .Create("call-1");

        var result = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

        Assert.Equal("the ordinary reply", result.ReplyText);
        Assert.Equal(1, model.Calls);

        var entry = Assert.Single(logger.Of(7));
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    [Fact]
    public async Task AnEndpointThatFails_WritesNoFlagAndNeverTheWordUnknown()
    {
        InMemoryAuditSink sink = new();
        var session = Build(PlainYaml, new SequencedChatClient("the ordinary reply"), sink: sink,
            moderation: ScriptedModerationEvaluator.Throwing(new InvalidOperationException("boom")))
            .Create("call-1");

        await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

        // A missing fact is an absent event, never an event carrying "unknown". The chain rule would
        // refuse a prompt.flagged with no category anyway.
        Assert.DoesNotContain(sink.EventsOf("call-1"), e => e.Kind == AuditEventKind.PromptFlagged);
    }

    [Fact]
    public async Task ASessionWithNoModerator_RunsATurnAndWritesNoFlag()
    {
        InMemoryAuditSink sink = new();
        var model = new SequencedChatClient("the ordinary reply");
        var session = Build(PlainYaml, model, sink: sink).Create("call-1");

        var result = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

        Assert.Equal("the ordinary reply", result.ReplyText);
        Assert.Equal(2, sink.EventsOf("call-1").Count);
    }

    // -------------------------------------------------------------------------------------------
    // The streaming path takes the same decision.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task AFlaggedPrompt_StreamsTheRefusalAndOpensNoModelStream()
    {
        InMemoryAuditSink sink = new();
        var model = new SequencedChatClient("never spoken");
        var session = Build(PlainYaml, model, sink: sink,
            moderation: ScriptedModerationEvaluator.Flagging("harassment")).Create("call-1");

        List<string> spoken = [];
        await foreach (var update in session.RunTurnStreamingAsync("...", TestContext.Current.CancellationToken))
        {
            spoken.Add(update.Text);
        }

        Assert.Equal(["I am sorry. I cannot help with that request."], spoken);
        Assert.Equal(0, model.Calls);
        Assert.Contains(sink.EventsOf("call-1"), e => e.Kind == AuditEventKind.PromptFlagged);
    }

    [Fact]
    public async Task ACleanPromptOnTheStreamingPath_ReachesTheModel()
    {
        var model = new SequencedChatClient("the ordinary reply");
        var session = Build(PlainYaml, model, moderation: ScriptedModerationEvaluator.Clean()).Create("call-1");

        await foreach (var _ in session.RunTurnStreamingAsync("hello", TestContext.Current.CancellationToken))
        {
            // Drain it.
        }

        Assert.Equal(1, model.Calls);
    }

    // -------------------------------------------------------------------------------------------
    // Helpers.
    // -------------------------------------------------------------------------------------------
    private static CallSessionFactory Build(
        string yaml,
        IChatClient reply,
        IChatClient? fill = null,
        IAuditSinkPort? sink = null,
        ILogger? logger = null,
        ScriptedModerationEvaluator? moderation = null)
    {
        // There is always a sink now: CallObservers.Standard takes a required one, because the
        // composition root resolves providers.audit for every host and falls back to the in-process
        // memory kind. An optional parameter has to be a compile-time constant, so the default is
        // spelled here instead — a fact that does not care where its events land gets a fresh
        // in-memory sink and reads exactly as it did when it passed nothing.
        IAuditSinkPort auditSink = sink ?? new InMemoryAuditSink();

        var document = ConfigurationLoader.LoadYaml(yaml);
        RoutingChatClientFactory chatClients = new(reply);
        if (fill is not null)
        {
            chatClients.Route("fill", fill);
        }

        // R3 puts moderation in the chat pipeline of every compiled agent, so the moderator is
        // bound at compile time and not on the session factory.
        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients)
            {
                Moderation = moderation is null ? null : new PromptModerator(moderation),
            });

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            CallSessionFactory.CreateExtractor(compiled, chatClients),
            timeProvider: null,
            logger,
            CallObservers.Standard(auditSink, logger));
    }
}

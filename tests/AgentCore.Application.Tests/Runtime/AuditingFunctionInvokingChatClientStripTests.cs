using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tools.Binding;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// K42's strip: <see cref="AuditingFunctionInvokingChatClient.InvokeFunctionAsync"/> hands a
/// nested tool call the turn with its <see cref="Clarifications"/> stripped, and hands the
/// outermost call the whole turn.
/// </summary>
/// <remarks>
/// The first two facts drive <see cref="AuditingFunctionInvokingChatClient"/> directly, the same way
/// <see cref="AuditingFunctionInvokingChatClientRenderTests"/> does, with the turn filed
/// on the call's own options. The third drives a real <see cref="CallSession"/> turn,
/// because that is the only place that proves the session actually threads its
/// own <c>Clarifications</c> instance through — a fact the first two cannot see, since they file the
/// turn by hand.
/// </remarks>
public sealed class AuditingFunctionInvokingChatClientStripTests
{
    [Fact]
    public async Task TheCallersOwnToolCall_SeesTheHolder()
    {
        var clarifications = new Clarifications();
        Clarifications? seen = null;

        var tool = AIFunctionFactory.Create(
            (TurnInvocation? turn) =>
            {
                seen = turn?.Clarifications;
                return "done.";
            },
            new AIFunctionFactoryOptions
            {
                Name = "search_tool",
                Description = "Reads the turn.",
                ConfigureParameterBinding = ToolParameterBindings.For,
            });

        var invocation = TurnOf(clarifications);

        ToolCallingChatClient inner = new("the loop continues.");
        using AuditingFunctionInvokingChatClient client = new(inner);
        ChatOptions options = new()
        {
            Tools = [tool],
            AdditionalProperties = new AdditionalPropertiesDictionary { [TurnInvocation.ArgumentsKey] = invocation },
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "search")], options, TestContext.Current.CancellationToken);

        Assert.Same(clarifications, seen);
    }

    [Fact]
    public async Task ANestedToolCall_HasNoHolder_WhileTheOuterCallStillDoes()
    {
        // The outer tool's own invocation reads the filed turn before starting the nested loop, and the
        // inner tool reads it from inside that nested loop's own InvokeFunctionAsync. The two reads
        // must disagree: K42's strip is conditional on being nested, not on being any tool call at
        // all, or it would blind the caller's own search too.
        var clarifications = new Clarifications();
        Clarifications? seenOutside = null;
        Clarifications? seenNested = null;

        var innerTool = AIFunctionFactory.Create(
            (TurnInvocation? turn) =>
            {
                seenNested = turn?.Clarifications;
                return "inner done.";
            },
            new AIFunctionFactoryOptions
            {
                Name = "inner_tool",
                Description = "Reads the turn from inside a nested loop.",
                ConfigureParameterBinding = ToolParameterBindings.For,
            });

        var outerTool = AIFunctionFactory.Create(
            async (TurnInvocation? outerTurn) =>
            {
                seenOutside = outerTurn?.Clarifications;

                ToolCallingChatClient innerModel = new("nested done.");
                using AuditingFunctionInvokingChatClient innerClient = new(innerModel);
                ChatOptions innerOptions = new()
                {
                    Tools = [innerTool],
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        [TurnInvocation.ArgumentsKey] = outerTurn! with { Nested = true, Clarifications = null },
                    },
                };
                await innerClient.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, "go")], innerOptions, TestContext.Current.CancellationToken);

                return "outer done.";
            },
            new AIFunctionFactoryOptions
            {
                Name = "outer_tool",
                Description = "Runs a nested loop.",
                ConfigureParameterBinding = ToolParameterBindings.For,
            });

        var invocation = TurnOf(clarifications);

        ToolCallingChatClient outerModel = new("done.");
        using AuditingFunctionInvokingChatClient outerClient = new(outerModel);
        ChatOptions outerOptions = new()
        {
            Tools = [outerTool],
            AdditionalProperties = new AdditionalPropertiesDictionary { [TurnInvocation.ArgumentsKey] = invocation },
        };

        await outerClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "go")], outerOptions, TestContext.Current.CancellationToken);

        Assert.Same(clarifications, seenOutside);
        Assert.Null(seenNested);
    }

    private static TurnInvocation TurnOf(Clarifications clarifications) => new()
    {
        CallId = "call",
        TurnIndex = 0,
        Stage = "",
        Clarifications = clarifications,
    };

    // -------------------------------------------------------------------------------------------
    // The wiring fact: a real CallSession turn opens its own Clarifications and files it on the
    // run, so the probe — not the store — sees it. The increment below proves both halves: no
    // holder, or no filing, and the probe's search never runs.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task RunTurnAsync_AToolModeKnowledgeSearch_SeesTheHolder()
    {
        const string yaml = """
            apiVersion: agentcore/v1
            name: clarifications-through-callsession
            state:
              applies_to:
                type: string
                writer: extractor
                description: "The model, as printed on the machine."
                enum: [CT900, CT900ENT]
              brand:
                type: string
                writer: extractor
                description: "The brand of the caller's machine."
                enum: [sole, spirit]
            extractor:
              model: { ref: fill }
              when: after_reply
            providers:
              call:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              knowledge:
                kind: qdrant
                collection: kb
                fields: { body: text }
                scope:
                  template: "{key}"
                  fromState: [applies_to, brand]
                  wildcard: { value: "*", facets: [applies_to, brand] }
                ambiguity: { maxCandidates: 6, maxAsks: 2 }
            agents:
              defaults:
                model: { ref: reply }
              items:
                - id: only
                  knowledge: { mode: tool, scoped: true }
            """;

        var port = new StubKnowledgePort([]);

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(yaml),
            new AgentCompilationContext(new FakeChatClientFactory(
                new ToolCallingChatClient(
                    "done.",
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["userQuestion"] = "what is it" })))
            {
                Knowledge = port,
            });

        var factory = new CallSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards));
        var session = factory.Create("call-strip-1");

        await session.RunTurnAsync("what is it", TestContext.Current.CancellationToken);

        Assert.Equal(2, port.Calls);
        Assert.Equal(1, session.Clarifications.Read("applies_to").ProbeAsks);
    }

    private static KnowledgeCard Card(string id)
        => new()
        {
            CardId = id,
            Text = "card " + id,
            Authority = 3,
            SourceRef = "ct900-om",
            SourceLocator = "p.27",
            Score = 0.87,
            ViaLink = false,
        };
}

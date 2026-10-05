using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Tools.Binding;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Knowledge.Fakes;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Clarification;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.ToolCalls;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// <see cref="AuditingFunctionInvokingChatClient.InvokeFunctionAsync"/> hands a nested tool call the turn
    /// with its <see cref="Clarifications"/> stripped, and hands the outermost call the whole turn.
    /// </summary>
    public sealed class AuditingFunctionInvokingChatClientStripTests
    {
        [Fact]
        public async Task TheCallersOwnToolCall_SeesTheHolder()
        {
            Clarifications clarifications = new();
            Clarifications? seen = null;

            AIFunction tool = AIFunctionFactory.Create(
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

            TurnInvocation invocation = TurnOf(clarifications);

            ToolCallingChatClient inner = new("the loop continues.");
            using AuditingFunctionInvokingChatClient client = new(inner);
            ChatOptions options = new()
            {
                Tools = [tool],
                AdditionalProperties = new AdditionalPropertiesDictionary { [TurnInvocation.ArgumentsKey] = invocation },
            };

            _ = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "search")], options, TestContext.Current.CancellationToken);

            Assert.Same(clarifications, seen);
        }

        [Fact]
        public async Task ANestedToolCall_HasNoHolder_WhileTheOuterCallStillDoes()
        {
            // The outer tool's own invocation reads the filed turn before starting the nested loop, and the
            // inner tool reads it from inside that nested loop's own InvokeFunctionAsync. The two reads
            // must disagree: the strip is conditional on being nested, not on being any tool call at
            // all, or it would blind the caller's own search too.
            Clarifications clarifications = new();
            Clarifications? seenOutside = null;
            Clarifications? seenNested = null;

            AIFunction innerTool = AIFunctionFactory.Create(
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

            AIFunction outerTool = AIFunctionFactory.Create(
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
                    _ = await innerClient.GetResponseAsync(
                        [new ChatMessage(ChatRole.User, "go")], innerOptions, TestContext.Current.CancellationToken);

                    return "outer done.";
                },
                new AIFunctionFactoryOptions
                {
                    Name = "outer_tool",
                    Description = "Runs a nested loop.",
                    ConfigureParameterBinding = ToolParameterBindings.For,
                });

            TurnInvocation invocation = TurnOf(clarifications);

            ToolCallingChatClient outerModel = new("done.");
            using AuditingFunctionInvokingChatClient outerClient = new(outerModel);
            ChatOptions outerOptions = new()
            {
                Tools = [outerTool],
                AdditionalProperties = new AdditionalPropertiesDictionary { [TurnInvocation.ArgumentsKey] = invocation },
            };

            _ = await outerClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "go")], outerOptions, TestContext.Current.CancellationToken);

            Assert.Same(clarifications, seenOutside);
            Assert.Null(seenNested);
        }

        private static TurnInvocation TurnOf(Clarifications clarifications)
        {
            return new()
            {
                ConversationId = "conversation",
                TurnIndex = 0,
                Stage = "",
                Clarifications = clarifications,
            };
        }

        // A real ConversationSession turn opens its own Clarifications and files it on the
        // run, so the probe — not the store — sees it. The increment below proves both halves: no
        // holder, or no filing, and the probe's search never runs.
        [Fact]
        public async Task RunTurnAsync_AToolModeKnowledgeSearch_SeesTheHolder()
        {
            const string yaml = """
            apiVersion: agentcore/v1
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
              conversation:   { kind: telnyx-relay }
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
            entries:
              main:
                agent: only
            """;

            StubKnowledgePort port = new([]);

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(yaml),
                new AgentCompilationContext(new FakeChatClientFactory(
                    new ToolCallingChatClient(
                        "done.",
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["userQuestion"] = "what is it" })))
                {
                    Knowledge = port,
                })["main"];

            ConversationSessionFactory factory = new(compiled, new GuardEvaluator(compiled.Configuration.Guards));
            ConversationSession session = factory.Create("conversation-strip-1");

            _ = await session.RunTurnAsync("what is it", TestContext.Current.CancellationToken);

            Assert.Equal(2, port.Calls);
            Assert.Equal(1, session.Clarifications.Read("applies_to").ProbeAsks);
        }
    }
}

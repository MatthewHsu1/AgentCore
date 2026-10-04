using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Diagnostics;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Diagnostics.LibraryOpenTelemetryHarness;

namespace AgentCore.Application.Tests.Diagnostics
{
    /// <summary>
    /// Microsoft.Extensions.AI's and Microsoft.Agents.AI's own GenAI instrumentation, switched on for every
    /// compiled agent.
    /// </summary>
    public sealed class LibraryOpenTelemetryTests
    {
        [Fact]
        public async Task AToolCallingTurn_NestsExecuteToolUnderInvokeAgentAndNotBesideIt()
        {
            string agentId = "agent-" + Guid.NewGuid().ToString("N");
            string yaml = $$"""
            apiVersion: agentcore/v1
            tools:
              - { id: lookup_order, kind: builtin, uses: orders.read, description: "Look up an order by its id." }
            agents:
              items:
                - { id: {{agentId}}, instructions: "answer questions", tools: [ lookup_order ] }
            entries:
              main:
                agent: {{agentId}}
            """;

            List<Activity> spans = [];
            using ActivityListener listener = ListenToLibrarySources(spans);

            using ToolCallingChatClient client = new("the order shipped.");
            StubToolBuilder tools = new(/*lang=json,strict*/ """{ "status": "shipped" }""");

            ConversationSession session = Build(yaml, client, tools.Create).Create();

            _ = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            List<Activity> mine = Snapshot(spans);

            // OpenTelemetryAgent.RunCoreAsync relabels the span its own OpenTelemetryChatClient opens, so
            // the operation this test looks for lives on DisplayName ("invoke_agent {name}({id})"), not on
            // Activity.OperationName (which stays "chat" — verified by decompiling
            // Microsoft.Extensions.AI.OpenTelemetryChatClient.GetResponseAsync).
            Activity invokeAgent = Assert.Single(
                mine, span => span.DisplayName.StartsWith("invoke_agent " + agentId, StringComparison.Ordinal));

            // The listener subscribes to the whole process, and xUnit runs other test classes beside this
            // one that also compile agents on these same two default source names. Everything from here
            // down is scoped to children of THIS run's own invoke_agent (a per-test unique W3C span id),
            // so a concurrent test's spans on the same sources cannot leak into these counts.
            List<Activity> underInvokeAgent = [.. mine.Where(span => string.Equals(span.ParentId, invokeAgent.Id, StringComparison.Ordinal))];

            // FunctionInvocationProcessor names its own span "execute_tool {toolName}" directly through
            // ActivitySource.StartActivity, so OperationName is reliable here.
            Activity executeTool = Assert.Single(
                underInvokeAgent, span => span.OperationName.StartsWith("execute_tool", StringComparison.Ordinal));

            // The proof: execute_tool's parent IS invoke_agent, by object
            // identity and by W3C parent id — not a sibling of it, and not nested inside the whole
            // tool-calling loop as one "chat" span. See the "Chat
            // telemetry is wired here" remark on WithToolFailureAuditing for why this holds:
            // the per-round chat span (source "Experimental.Microsoft.Extensions.AI") closes before the
            // tool runs, so Activity.Current has reverted to invoke_agent by the time execute_tool opens.
            Assert.Same(invokeAgent, executeTool.Parent);

            // And the per-round chat spans exist too (one for the round that decided to call the tool, one
            // for the round that answered after the tool result), as siblings of execute_tool under the
            // same parent — not merged into the tool loop, and not swallowing it.
            List<Activity> chatRounds = [.. underInvokeAgent.Where(IsChatRoundSpan)];
            Assert.Equal(2, chatRounds.Count);
        }

        [Fact]
        public async Task EveryCompiledAgent_IsInstrumentedExactlyOnce()
        {
            string frontId = "front-" + Guid.NewGuid().ToString("N");
            string specialistId = "specialist-" + Guid.NewGuid().ToString("N");
            string yaml = $$"""
              apiVersion: agentcore/v1
              tools:
                - { id: ask_specialist, kind: agent, agent: {{specialistId}}, description: Ask the specialist. }
              agents:
                items:
                  - { id: {{frontId}}, instructions: "the caller talks to me", tools: [ ask_specialist ] }
                  - { id: {{specialistId}}, instructions: "I answer product questions" }
              entries:
                main:
                  policy:
                    initial: talk
                    stages:
                      - { id: talk, agent: {{frontId}}, terminal: true }
              """;

            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);

            // Reachable structurally, with no turn run at all: ConfigurationCompiler.Resolve wraps the
            // agent it just built, once, immediately before it caches it in the dictionary every later
            // lookup (the stage lookup, and the delegation tool's ResolveInner) reads back out of. A
            // second wrap would require Resolve to reach the "var built = new ChatClientAgent(...)" branch
            // twice for one id, and the early "agents.TryGetValue" return above it is what this test would
            // catch failing to hold.
            using ToolCallingChatClient buildOnlyClient = new("unused");
            CompiledAgent compiledOnly = ConfigurationCompiler.CompileAll(
                document, new AgentCompilationContext(new FakeChatClientFactory(buildOnlyClient)))["main"];

            _ = Assert.IsType<OpenTelemetryAgent>(compiledOnly.Agents[frontId]);
            _ = Assert.IsType<ChatClientAgent>(compiledOnly.Agents[frontId].GetService<ChatClientAgent>());
            _ = Assert.IsType<OpenTelemetryAgent>(compiledOnly.Agents[specialistId]);
            _ = Assert.IsType<ChatClientAgent>(compiledOnly.Agents[specialistId].GetService<ChatClientAgent>());

            // The behavioural half of the same proof: running a turn that calls through the delegation
            // path produces exactly one invoke_agent span for each agent. Two OpenTelemetryAgent layers
            // around the same ChatClientAgent would double whichever agent got wrapped twice; a missing
            // wrap would mean zero.
            List<Activity> spans = [];
            using ActivityListener listener = ListenToLibrarySources(spans);

            // AsAIFunction() generates one required string argument named "query" (no parameters: is
            // declared on ask_specialist), so the model call must fill it or the delegation throws
            // ArgumentException before ever reaching the specialist agent.
            using ToolCallingChatClient client = new(
                "the specialist answer", new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "help me" });
            ConversationSession session = Build(yaml, client, tools: null).Create();

            _ = await session.RunTurnAsync("help me", TestContext.Current.CancellationToken);

            List<Activity> mine = Snapshot(spans);

            _ = Assert.Single(mine, span => span.DisplayName.StartsWith("invoke_agent " + frontId, StringComparison.Ordinal));
            _ = Assert.Single(
                mine, span => span.DisplayName.StartsWith("invoke_agent " + specialistId, StringComparison.Ordinal));
        }

        [Fact]
        public void EveryCompiledAgent_KeepsSensitiveDataCaptureOffOnBothLayers()
        {
            string agentId = "agent-" + Guid.NewGuid().ToString("N");
            string yaml = $$"""
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: {{agentId}} }
            entries:
              main:
                agent: {{agentId}}
            """;

            using ToolCallingChatClient client = new("hello");
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(yaml), new AgentCompilationContext(new FakeChatClientFactory(client)))["main"];

            OpenTelemetryAgent otelAgent = Assert.IsType<OpenTelemetryAgent>(compiled.Agent);

            // OpenTelemetryAgent.EnableSensitiveData otherwise defaults to
            // TelemetryHelpers.EnableSensitiveDataDefault, which reads
            // OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT from the environment. This repo carries
            // live customer phone conversations, so ConfigurationCompiler forces it false explicitly rather than
            // trusting that variable to stay unset.
            Assert.False(otelAgent.EnableSensitiveData);

            // The chat-level layer WithToolFailureAuditing wires in below AuditingFunctionInvokingChatClient
            // is a second, independent OpenTelemetryChatClient instance with its own EnableSensitiveData,
            // and it is forced off the same way, at the same conversation site.
            OpenTelemetryChatClient? chatClient = compiled.Agent.GetService<OpenTelemetryChatClient>();
            Assert.NotNull(chatClient);
            Assert.False(chatClient.EnableSensitiveData);
        }

        /// <summary>
        /// The conversation id reaches a trace once, from <c>AgentCoreTelemetry.StartTurn</c>.
        /// </summary>
        [Fact]
        public async Task ASingleAgentTurn_CarriesTheConversationIdOnTheTurnSpanAndOnNeitherLibrarySpan()
        {
            string agentId = "agent-" + Guid.NewGuid().ToString("N");
            string yaml = $$"""
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: {{agentId}} }
            entries:
              main:
                agent: {{agentId}}
            """;

            List<Activity> spans = [];
            using ActivityListener libraries = ListenToLibrarySources(spans);
            using ActivityListener turns = ListenTo(spans, AgentCoreTelemetry.ActivitySourceName);

            using ToolCallingChatClient client = new("hello there.");
            string conversationId = "conversation-" + Guid.NewGuid().ToString("N");
            ConversationSession session = Build(yaml, client, tools: null).Create(conversationId);

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            List<Activity> mine = Snapshot(spans);

            Activity invokeAgent = Assert.Single(
                mine, span => span.DisplayName.StartsWith("invoke_agent " + agentId, StringComparison.Ordinal));

            // Scoped to this run's own invoke_agent for the same reason as the nesting test above: other
            // test classes compile agents on these same two sources concurrently.
            Activity chat = Assert.Single(
                mine, span => IsChatRoundSpan(span) && string.Equals(span.ParentId, invokeAgent.Id, StringComparison.Ordinal));

            Activity turn = Assert.Single(
                mine,
                span => span.DisplayName == AgentCoreTelemetry.TurnActivityName
                    && string.Equals(span.Id, invokeAgent.ParentId, StringComparison.Ordinal));

            Assert.Equal(conversationId, turn.GetTagItem("gen_ai.conversation.id"));
            Assert.Null(invokeAgent.GetTagItem("gen_ai.conversation.id"));
            Assert.Null(chat.GetTagItem("gen_ai.conversation.id"));
        }

        /// <summary>
        /// One turn makes two model calls, and both of them emit a chat span.
        /// </summary>
        [Fact]
        public async Task ATurnWithAnExtractor_EmitsAChatSpanForTheExtractorCallAndNotOnlyForTheReply()
        {
            string greeterId = "greeter-" + Guid.NewGuid().ToString("N");
            string closerId = "closer-" + Guid.NewGuid().ToString("N");
            string yaml = $$"""
              apiVersion: agentcore/v1
              state:
                callerSaidGoodbye:
                  type: boolean
                  default: false
                  writer: extractor
                  description: whether the caller said goodbye
              guards:
                saidGoodbye: { var: callerSaidGoodbye }
              extractor:
                model: { ref: fill }
                when: after_reply
              agents:
                defaults:
                  model: { ref: reply }
                items:
                  - { id: {{greeterId}}, instructions: "greet the caller" }
                  - { id: {{closerId}},  instructions: "close the conversation" }
              entries:
                main:
                  policy:
                    initial: greeting
                    stages:
                      - id: greeting
                        agent: {{greeterId}}
                        to: [ { stage: close, when: saidGoodbye } ]
                      - id: close
                        agent: {{closerId}}
                        terminal: true
              """;

            List<Activity> spans = [];
            using ActivityListener libraries = ListenToLibrarySources(spans);
            using ActivityListener turns = ListenTo(spans, AgentCoreTelemetry.ActivitySourceName);

            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(/*lang=json,strict*/ """{ "callerSaidGoodbye": null }""");

            string conversationId = "conversation-" + Guid.NewGuid().ToString("N");
            ConversationSession session = BuildWithExtractor(yaml, reply, fill).Create(conversationId);

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // The premise: two clients, one call each. If this ever reads differently the span counts
            // below mean nothing.
            Assert.Equal(1, reply.Calls);
            Assert.Equal(1, fill.Calls);

            List<Activity> mine = Snapshot(spans);

            // Scoped by this run's own conversation id, because the listener subscribes to the whole process.
            Activity turn = Assert.Single(
                mine,
                span => span.DisplayName == AgentCoreTelemetry.TurnActivityName
                    && string.Equals((string?)span.GetTagItem("gen_ai.conversation.id"), conversationId, StringComparison.Ordinal));

            Activity invokeAgent = Assert.Single(
                mine,
                span => span.DisplayName.StartsWith("invoke_agent " + greeterId, StringComparison.Ordinal)
                    && string.Equals(span.ParentId, turn.Id, StringComparison.Ordinal));

            // The reply's chat span sits under invoke_agent, where ConfigurationCompiler puts it.
            _ = Assert.Single(
                mine,
                span => IsChatRoundSpan(span) && string.Equals(span.ParentId, invokeAgent.Id, StringComparison.Ordinal));

            // The extractor's chat span is the one this test exists for. It hangs directly off the turn
            // span and not off invoke_agent, because ConversationSession runs the extractor after the agent's run
            // has finished and its spans have closed, with Activity.Current back at agentcore.turn.
            Activity extractorChat = Assert.Single(
                mine,
                span => IsChatRoundSpan(span) && string.Equals(span.ParentId, turn.Id, StringComparison.Ordinal));

            // Instrumented the same way as every other model call in this library: the default source
            // name, so a host already receiving agent chat spans receives this one with no second
            // AddSource, and no message content on the span.
            Assert.Equal(ChatSourceName, extractorChat.Source.Name);
            Assert.Null(extractorChat.GetTagItem("gen_ai.input.messages"));
        }
    }
}

using AgentCore.TestSupport;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Binding;
using AgentCore.Application.Tools.Registry;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Domain;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// The conversation workspace as a host sees it: bound (or not) through <see cref="ConversationSessionFactory"/>,
    /// deleted when the conversation ends, and visible to a bound tool through <see cref="ToolCallScope"/>.
    /// </summary>
    public sealed class ConversationSessionWorkspaceTests : IDisposable
    {
        private const string SimpleYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        entries:
          main:
            agent: only
        """;

        private const string TerminalYaml =
            """
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
              - { id: greeter, instructions: "greet the caller" }
              - { id: closer,  instructions: "close the conversation" }
          entries:
            main:
              policy:
                initial: greeting
                stages:
                  - id: greeting
                    agent: greeter
                    to: [ { stage: close, when: saidGoodbye } ]
                  - id: close
                    agent: closer
                    terminal: true
          """;

        private const string ScopeYaml =
            """
        apiVersion: agentcore/v1
        tools:
          - { id: request_human, kind: binding, binds: RequestHuman, description: "Ask a human to take the conversation." }
        agents:
          items:
            - { id: only, instructions: "help the caller", tools: [ request_human ] }
        entries:
          main:
            agent: only
        """;

        private readonly string _root = Path.Combine(Path.GetTempPath(), "agentcore-ws-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Fact]
        public void ARootBound_CreatesTheConversationsFolder()
        {
            using SequencedChatClient reply = new("hi there.");
            ConversationSessionFactory factory = Build(SimpleYaml, reply, fill: null, _root);

            ConversationSession session = factory.Create("conversation-1");

            Assert.Equal(Path.Combine(_root, "conversation-1"), session.Workspace);
            Assert.True(Directory.Exists(session.Workspace));
        }

        [Fact]
        public void NoRootBound_LeavesWorkspaceNull_AndCreatesNothing()
        {
            using SequencedChatClient reply = new("hi there.");
            ConversationSessionFactory factory = Build(SimpleYaml, reply, fill: null, workspaceRoot: null);

            ConversationSession session = factory.Create("conversation-1");

            Assert.Null(session.Workspace);
            Assert.False(Directory.Exists(_root));
        }

        [Fact]
        public void EndConversation_DeletesTheFolder_AndIsSafeToCallTwice()
        {
            using SequencedChatClient reply = new("hi there.");
            ConversationSessionFactory factory = Build(SimpleYaml, reply, fill: null, _root);
            ConversationSession session = factory.Create("conversation-1");
            string path = session.Workspace!;

            _ = session.EndConversation(ConversationEndReason.CallerHungUp);

            Assert.False(Directory.Exists(path));
            Assert.False(session.EndConversation(ConversationEndReason.CallerHungUp));
        }

        [Fact]
        public async Task ATurnThatReachesATerminalStage_AlsoDeletesTheFolder()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(/*lang=json,strict*/ """{ "callerSaidGoodbye": true }""");
            ConversationSessionFactory factory = Build(TerminalYaml, reply, fill, _root);
            ConversationSession session = factory.Create("conversation-1");
            string path = session.Workspace!;

            TurnResult turn = await session.RunTurnAsync("goodbye", TestContext.Current.CancellationToken);

            Assert.True(turn.IsTerminal);
            Assert.True(session.IsComplete);
            Assert.False(Directory.Exists(path));
        }

        [Fact]
        public async Task ABoundTool_SeesTheRunningConversationsWorkspace()
        {
            List<ToolCallScope> captured = [];
            ToolBindingRegistry bindings = new();
            _ = bindings.Register("RequestHuman", (string reason, ToolCallScope scope) => captured.Add(scope));

            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(ScopeYaml);
            RoutingChatClientFactory chatClients = new(
                new ToolCallingChatClient(
                    "connecting you now.",
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["reason"] = "the caller wants a person" }));
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients)
                {
                    Tools = await ToolRegistryBuilder.BuildAsync(
                        [new BindingToolSource(bindings)],
                        new ToolSourceContext(document),
                        TestContext.Current.CancellationToken),
                })["main"];

            ConversationSessionFactory factory = new(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                workspaceRoot: _root);
            ConversationSession session = factory.Create("conversation-1");

            _ = await session.RunTurnAsync("I need a person", TestContext.Current.CancellationToken);

            ToolCallScope scope = Assert.Single(captured);
            Assert.Equal(session.Workspace, scope.Workspace);
            Assert.NotNull(scope.Workspace);
        }

        private static ConversationSessionFactory Build(
            string yaml,
            IChatClient reply,
            IChatClient? fill,
            string? workspaceRoot)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            RoutingChatClientFactory chatClients = new(reply);
            if (fill is not null)
            {
                _ = chatClients.Route("fill", fill);
            }

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients)
                {
                    Tools = TestToolRegistry.From(document, builder: null, TestContext.Current.CancellationToken),
                })["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                ConversationSessionFactory.CreateExtractor(compiled, chatClients),
                workspaceRoot: workspaceRoot);
        }
    }
}

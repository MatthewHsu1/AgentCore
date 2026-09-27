using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Runtime;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// Bug 1 of the 2026-09-24 round: a graph node's checkpoint must resume under a second compile of the
    /// same document (a restart, or a second host). MAF's <c>InProcessRunner</c> matches a checkpoint by
    /// executor id, and an agent's executor id folds in <see cref="ChatClientAgentOptions.Id"/>
    /// (<c>AIAgentExtensions.GetDescriptiveId</c>). <see cref="ConfigurationCompiler"/> gives every agent a
    /// stable id, so two compiles of one document bind the same executor ids.
    /// </summary>
    public sealed class ConversationSessionGraphIdTests
    {
        private const string SequentialTodos =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: adder, instructions: "track todos", todos: true, model: { ref: adder } }
              - { id: echo, instructions: "echo back", model: { ref: echo } }
          entries:
            main:
              graph:
                pattern: sequential
                agents: [ adder, echo ]
          """;

        private const string ExplicitTodos =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: adder, instructions: "track todos", todos: true, model: { ref: adder } }
              - { id: echo, instructions: "echo back", model: { ref: echo } }
          entries:
            main:
              graph:
                nodes:
                  - { id: start, agent: adder, start: true }
                  - { id: finish, agent: echo, output: true }
                edges:
                  - { from: start, to: finish }
          """;

        private const string PatternDashUnderscore =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: order-lookup, instructions: "one", model: { ref: dash } }
              - { id: order_lookup, instructions: "two", model: { ref: under } }
          entries:
            main:
              graph:
                pattern: sequential
                agents: [ order-lookup, order_lookup ]
          """;

        private const string ExplicitDashUnderscore =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: order-lookup, instructions: "one", model: { ref: dash } }
              - { id: order_lookup, instructions: "two", model: { ref: under } }
          entries:
            main:
              graph:
                nodes:
                  - { id: start,  agent: order-lookup, start: true }
                  - { id: finish, agent: order_lookup, output: true }
                edges:
                  - { from: start, to: finish }
          """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>
        /// Claim: <c>order-lookup</c> and <c>order_lookup</c> are distinct, schema-valid ids. Before the fix,
        /// <c>Id = item.Id</c> let MAF's sanitizer (<c>Regex.Replace(Name + "_" + Id, "[^0-9A-Za-z]+", "_")</c>)
        /// collapse them to the same executor id, and the compile threw. Both nodes must run and the graph
        /// must answer with the second node's reply.
        /// </summary>
        [Theory]
        [InlineData(PatternDashUnderscore)]
        [InlineData(ExplicitDashUnderscore)]
        public async Task Graph_TwoAgentIdsDifferingOnlyByDashAndUnderscore_BothRun(string yaml)
        {
            RoutingChatClientFactory clients = new(new TextModel("from dash"));
            _ = clients.Route("dash", new TextModel("from dash"));
            _ = clients.Route("under", new TextModel("from under"));
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(yaml),
                new AgentCompilationContext(clients) { ConversationStore = new InMemoryConversationStore() })["main"];
            ConversationSession session = new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards)).Create("c1");

            TurnResult turn = await session.RunTurnAsync("go", Ct);

            Assert.Null(turn.Failure);
            Assert.Equal("from under", turn.ReplyText);
        }

        private static ConversationSessionFactory Factory(string yaml, InMemoryConversationStore store)
        {
            RoutingChatClientFactory clients = new(new TextModel("added"));
            _ = clients.Route("adder", new TextModel("added"));
            _ = clients.Route("echo", new TextModel("echo"));
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(yaml),
                new AgentCompilationContext(clients) { ConversationStore = store })["main"];
            return new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards));
        }

        [Theory]
        [InlineData(SequentialTodos)]
        [InlineData(ExplicitTodos)]
        public async Task Graph_TwoCompilesAlternating_EveryTurnResumes(string yaml)
        {
            InMemoryConversationStore store = new();
            ConversationSession a = Factory(yaml, store).Create("g1");
            ConversationSession b = Factory(yaml, store).Create("g1");
            List<string> faults = [];

            foreach ((string who, ConversationSession s) in new[] { ("A", a), ("B", b), ("A", a), ("B", b), ("A", a), ("A", a), ("B", b) })
            {
                TurnResult t = await s.RunTurnAsync("hi", Ct);
                if (t.Failure is not null)
                {
                    faults.Add($"{who}{t.TurnIndex}: {t.Failure}");
                }
            }

            Assert.Empty(faults);
        }

        private sealed class TextModel(string text) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                string id = Guid.NewGuid().ToString("N");
                yield return new ChatResponseUpdate(ChatRole.Assistant, text) { ResponseId = id, MessageId = id };
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}

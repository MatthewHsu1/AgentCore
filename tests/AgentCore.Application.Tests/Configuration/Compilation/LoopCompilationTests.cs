using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Compilation;

/// <summary>
/// The <c>loop:</c> block reaching a compiled agent as a <see cref="LoopAgent"/>, the evaluator
/// each <c>until:</c> entry selects, and the failures a block names that its agent cannot honor.
/// </summary>
#pragma warning disable MAAI001 // The loop family is evaluation-only in Microsoft.Agents.AI 1.21.0.
public sealed class LoopCompilationTests
{
    private const string TodosLoopYaml =
        """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: coder, instructions: "fix bugs", todos: true, loop: { maxRounds: 3, until: [{ todos: {} }] } }
        entries:
          main:
            agent: coder
        """;

    private const string BackgroundLoopYaml =
        """
          apiVersion: agentcore/v1
          guards:
            always: { ">=": [ { var: turnIndex }, 0 ] }
          agents:
            items:
              - { id: parent, instructions: "delegate work", background: [coder], loop: { maxRounds: 2, until: [{ background: {} }] } }
              - { id: coder, instructions: "write code" }
          entries:
            main:
              policy:
                initial: working
                stages:
                  - { id: working, agent: parent, to: [ { stage: done, when: always } ] }
                  - { id: done, agent: coder, terminal: true }
          """;

      [Fact]
      public void Compile_LoopWithUntilTodosAndTodos_GetsALoopAgent()
      {
          var compiled = Compile(TodosLoopYaml);

          Assert.NotNull(compiled.Agents["coder"].GetService<LoopAgent>());
      }

      [Fact]
      public void Compile_NoLoop_GetsNoLoopAgent()
      {
          var compiled = ConfigurationCompiler.CompileAll(
              new AgentCoreConfiguration
              {
                  ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
                  Agents = new AgentsConfiguration
                  {
                      Items = [new AgentConfiguration { Id = "only", Todos = true }],
                  },
                  Entries = new Dictionary<string, EntryConfiguration>
                  {
                      ["main"] = new EntryConfiguration { Agent = "only" },
                  },
              },
              new AgentCompilationContext(new FakeChatClientFactory(new SequencedChatClient("hello there."))))["main"];

          Assert.Null(Assert.Single(compiled.Agents.Values).GetService<LoopAgent>());
      }

      [Fact]
      public async Task Compile_LoopWithMaxRoundsThreeAndOpenTodos_RunsThreeIterationsThenStops()
      {
          var client = new ToolCallingChatClient(
              "added",
              new Dictionary<string, object?>(StringComparer.Ordinal)
              {
                  ["todos"] = new object[]
                  {
                      new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = "T" },
                  },
              });

          var compiled = ConfigurationCompiler.CompileAll(
              ConfigurationLoader.LoadYaml(TodosLoopYaml),
              new AgentCompilationContext(new FakeChatClientFactory(client)))["main"];

          var agent = compiled.Agents["coder"];
          var token = TestContext.Current.CancellationToken;
          var session = await agent.CreateSessionAsync(token);

          var response = await agent.RunAsync("fix it", session, cancellationToken: token);

          // Each iteration replays the tool call and the text: iterations rerun from the initial
          // messages plus the evaluator's feedback while only the provider session state carries
          // over, so the open todo list keeps every iteration going. Three iterations at the
          // maxRounds: 3 cap, then the loop stops rather than running on. Without the wrap the
          // single run would spend two model calls and call todos_add once.
          Assert.Equal(3, client.Called.Count(name => name == "todos_add"));
          Assert.Equal(6, client.Calls);
          Assert.Contains("added", response.Text, StringComparison.Ordinal);
      }

      [Fact]
      public void Compile_LoopUntilTodosWithoutTodos_FailsNamingTheUntilPointer()
      {
          const string yaml =
              """
              apiVersion: agentcore/v1
              agents:
                items:
                  - { id: coder, instructions: "fix bugs", loop: { maxRounds: 3, until: [{ todos: {} }] } }
              entries:
                main:
                  agent: coder
              """;

          var failure = Assert.Throws<ConfigurationLoadException>(() => Compile(yaml));

          Assert.Equal("/agents/items/0/loop/until/0", failure.Pointer);
          Assert.Contains("todos:", failure.Message, StringComparison.Ordinal);
      }

      [Fact]
      public void Compile_LoopUntilBackgroundWithoutBackground_FailsNamingTheUntilPointer()
      {
          const string yaml =
              """
              apiVersion: agentcore/v1
              agents:
                items:
                  - { id: coder, instructions: "fix bugs", loop: { maxRounds: 3, until: [{ background: {} }] } }
              entries:
                main:
                  agent: coder
              """;

          var failure = Assert.Throws<ConfigurationLoadException>(() => Compile(yaml));

          Assert.Equal("/agents/items/0/loop/until/0", failure.Pointer);
          Assert.Contains("background:", failure.Message, StringComparison.Ordinal);
      }

      [Fact]
      public void Compile_LoopWithNoUntil_FailsNamingTheLoopPointer()
      {
          const string yaml =
              """
              apiVersion: agentcore/v1
              agents:
                items:
                  - { id: coder, instructions: "fix bugs", loop: { maxRounds: 3 } }
              entries:
                main:
                  agent: coder
              """;

          var failure = Assert.Throws<ConfigurationLoadException>(() => Compile(yaml));

          Assert.Equal("/agents/items/0/loop", failure.Pointer);
          Assert.Contains("until:", failure.Message, StringComparison.Ordinal);
      }

      [Fact]
      public void Compile_LoopWithBackgroundUntilAndChildren_CompilesAndAddsNoStateKeys()
      {
          using SequencedChatClient childReply = new("hi");
          var backgroundKeys = new BackgroundAgentsProvider(
              [new ChatClientAgent(childReply, new ChatClientAgentOptions { Name = "coder" })]).StateKeys;

          var compiled = Compile(BackgroundLoopYaml);

          Assert.NotNull(compiled.Agents["parent"].GetService<LoopAgent>());
          Assert.Equal(backgroundKeys.ToHashSet(StringComparer.Ordinal), compiled.HarnessStateKeys);
      }

      private static CompiledAgent Compile(string yaml) => ConfigurationCompiler.CompileAll(
          ConfigurationLoader.LoadYaml(yaml),
          new AgentCompilationContext(new FakeChatClientFactory(new SequencedChatClient("hello there."))))["main"];
  }
  #pragma warning restore MAAI001

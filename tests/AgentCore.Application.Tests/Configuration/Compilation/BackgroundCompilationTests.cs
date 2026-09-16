using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Compilation;

/// <summary>
/// The <c>background:</c> list reaching a compiled agent as a <see cref="BackgroundAgentsProvider"/>,
/// the <c>background_agents_*</c> tools it puts in front of the model, and the state key it
/// contributes to resume.
/// </summary>
#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
public sealed class BackgroundCompilationTests
{
    private static readonly string[] ExpectedBackgroundTools =
    [
        "background_agents_start_task",
        "background_agents_wait_for_first_completion",
        "background_agents_get_task_results",
        "background_agents_get_all_tasks",
        "background_agents_continue_task",
        "background_agents_clear_completed_task",
    ];

    private const string ParentWithChildYaml =
        """
          apiVersion: agentcore/v1
          guards:
            always: { ">=": [ { var: turnIndex }, 0 ] }
          agents:
            items:
              - { id: parent, instructions: "delegate work", background: [coder] }
              - { id: coder, instructions: "write code" }
          entries:
            main:
              policy:
                initial: working
                stages:
                  - { id: working, agent: parent, to: [ { stage: done, when: always } ] }
                  - { id: done, agent: coder, terminal: true }
          """;

      private const string ParentWithTodosAndChildYaml =
          """
          apiVersion: agentcore/v1
          guards:
            always: { ">=": [ { var: turnIndex }, 0 ] }
          agents:
            items:
              - { id: parent, instructions: "delegate work", todos: true, background: [coder] }
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
      public void Compile_BackgroundWithAChild_GetsABackgroundAgentsProvider()
      {
          var compiled = Compile(ParentWithChildYaml);

          var parent = compiled.Agents["parent"].GetService<ChatClientAgent>();
          Assert.NotNull(parent);
          Assert.Contains(parent.AIContextProviders ?? [], provider => provider is BackgroundAgentsProvider);
      }

      [Fact]
      public void Compile_NoBackground_GetsNoBackgroundAgentsProvider()
      {
          var compiled = ConfigurationCompiler.CompileAll(
              new AgentCoreConfiguration
              {
                  ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
                  Agents = new AgentsConfiguration
                  {
                      Items = [new AgentConfiguration { Id = "only" }],
                  },
                  Entries = new Dictionary<string, EntryConfiguration>
                  {
                      ["main"] = new EntryConfiguration { Agent = "only" },
                  },
              },
              new AgentCompilationContext(new FakeChatClientFactory(new SequencedChatClient("hello there."))))["main"];

          var agent = Assert.Single(compiled.Agents.Values).GetService<ChatClientAgent>();
          Assert.NotNull(agent);
          Assert.DoesNotContain(agent.AIContextProviders ?? [], provider => provider is BackgroundAgentsProvider);
      }

      [Fact]
      public async Task Compile_BackgroundWithAChild_ModelSeesTheSixBackgroundTools()
      {
          using SequencedChatClient reply = new("hello there.");

          var compiled = ConfigurationCompiler.CompileAll(
              ConfigurationLoader.LoadYaml(ParentWithChildYaml),
              new AgentCompilationContext(new FakeChatClientFactory(reply)))["main"];

          var agent = compiled.Agents["parent"];
          var token = TestContext.Current.CancellationToken;
          var session = await agent.CreateSessionAsync(token);

          await agent.RunAsync("hi", session, cancellationToken: token);

          var toolNames = reply.Options[^1]?.Tools?.Select(tool => tool.Name).ToArray() ?? [];

          Assert.Equal(ExpectedBackgroundTools, toolNames, StringComparer.Ordinal);
      }

      private const string ParentWithDescribedChildYaml =
          """
          apiVersion: agentcore/v1
          guards:
            always: { ">=": [ { var: turnIndex }, 0 ] }
          agents:
            items:
              - { id: parent, instructions: "delegate work", background: [coder] }
              - { id: coder, description: "write code fast", instructions: "write code" }
          entries:
            main:
              policy:
                initial: working
                stages:
                  - { id: working, agent: parent, to: [ { stage: done, when: always } ] }
                  - { id: done, agent: coder, terminal: true }
          """;

      [Fact]
      public async Task Compile_BackgroundChildWithDescription_ModelSeesTheDescriptionBesideTheId()
      {
          using SequencedChatClient reply = new("hello there.");

          var compiled = ConfigurationCompiler.CompileAll(
              ConfigurationLoader.LoadYaml(ParentWithDescribedChildYaml),
              new AgentCompilationContext(new FakeChatClientFactory(reply)))["main"];

          var agent = compiled.Agents["parent"];
          var token = TestContext.Current.CancellationToken;
          var session = await agent.CreateSessionAsync(token);

          await agent.RunAsync("hi", session, cancellationToken: token);

          var seen = string.Join(
              '\n',
              reply.Requests[^1].Select(message => message.Text).Prepend(reply.Options[^1]?.Instructions ?? string.Empty));

          Assert.Contains("- coder: write code fast", seen, StringComparison.Ordinal);
      }

      [Fact]
      public void Compile_BackgroundWithTodos_HarnessStateKeysIncludeTheBackgroundKey()
      {
          using SequencedChatClient childReply = new("hi");
          var backgroundKeys = new BackgroundAgentsProvider(
              [new ChatClientAgent(childReply, new ChatClientAgentOptions { Name = "coder" })]).StateKeys;

          var compiled = Compile(ParentWithTodosAndChildYaml);
          HashSet<string> expected = [new TodoProvider().StateKeys[0]];
          expected.UnionWith(backgroundKeys);

          Assert.Equal(expected, compiled.HarnessStateKeys);
      }

      [Fact]
      public void Compile_BackgroundChildDeclaredLater_ResolvesForward()
      {
          var compiled = Compile(ParentWithChildYaml);

          Assert.True(compiled.Agents.ContainsKey("coder"));
          var parent = compiled.Agents["parent"].GetService<ChatClientAgent>();
          Assert.NotNull(parent);
          Assert.Contains(parent.AIContextProviders ?? [], provider => provider is BackgroundAgentsProvider);
      }

      private const string UnknownChildYaml =
          """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: parent, instructions: "delegate work", background: [ghost] }
        entries:
          main:
            agent: parent
        """;

    [Fact]
    public void Compile_BackgroundUnknownChild_FailsNamingTheBackgroundPointer()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(() => Compile(UnknownChildYaml));

        Assert.Equal("/agents/items/0/background/0", failure.Pointer);
        Assert.Contains("not declared in agents.items", failure.Message, StringComparison.Ordinal);
    }

    private const string SelfChildYaml =
        """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: parent, instructions: "delegate work", background: [parent] }
        entries:
          main:
            agent: parent
        """;

    private const string CollidingChildYaml =
        """
          apiVersion: agentcore/v1
          guards:
            always: { ">=": [ { var: turnIndex }, 0 ] }
          agents:
            items:
              - { id: parent, instructions: "delegate work", background: [Coder, coder] }
              - { id: Coder, instructions: "write code loudly" }
              - { id: coder, instructions: "write code quietly" }
          entries:
            main:
              policy:
                initial: working
                stages:
                  - { id: working, agent: parent, to: [ { stage: done, when: always } ] }
                  - { id: done, agent: coder, terminal: true }
          """;

      [Fact]
      public void Compile_BackgroundChildrenWithCollidingNames_FailsNamingTheBackgroundPointer()
      {
          var failure = Assert.Throws<ConfigurationLoadException>(() => Compile(CollidingChildYaml));

          Assert.Equal("/agents/items/0/background", failure.Pointer);
      }

      private static CompiledAgent Compile(string yaml) => ConfigurationCompiler.CompileAll(
          ConfigurationLoader.LoadYaml(yaml),
          new AgentCompilationContext(new FakeChatClientFactory(new SequencedChatClient("hello there."))))["main"];
  }
  #pragma warning restore MAAI001

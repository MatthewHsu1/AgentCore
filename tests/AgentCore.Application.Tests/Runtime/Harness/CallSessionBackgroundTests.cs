using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Harness;

/// <summary>
/// The call-end release of background sessions: a child still running when the call closes or a
/// turn reaches a terminal stage is cancelled instead of leaking past the call.
/// </summary>
#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
public sealed class CallSessionBackgroundTests
{
    private const string LingeringChildYaml =
        """
          apiVersion: agentcore/v1
          guards:
            never: { "<": [ { var: turnIndex }, 0 ] }
          agents:
            items:
              - { id: parent, instructions: "delegate work", model: { ref: parent }, background: [blocker] }
              - { id: blocker, instructions: "work forever", model: { ref: blocker } }
          entries:
            main:
              policy:
                initial: working
                stages:
                  - { id: working, agent: parent, to: [ { stage: done, when: never } ] }
                  - { id: done, agent: blocker, terminal: true }
          """;

      private const string TerminalChildYaml =
          """
          apiVersion: agentcore/v1
          guards:
            always: { ">=": [ { var: turnIndex }, 0 ] }
          agents:
            items:
              - { id: parent, instructions: "delegate work", model: { ref: parent }, background: [blocker] }
              - { id: blocker, instructions: "work forever", model: { ref: blocker } }
          entries:
            main:
              policy:
                initial: working
                stages:
                  - { id: working, agent: parent, to: [ { stage: done, when: always } ] }
                  - { id: done, agent: blocker, terminal: true }
          """;

      [Fact]
      public async Task CloseAsync_WithAChildStillRunning_CancelsIt()
      {
          var token = TestContext.Current.CancellationToken;
          var parent = new ToolCallingChatClient(
              "started",
              new Dictionary<string, object?>(StringComparer.Ordinal)
              {
                  ["agentName"] = "blocker",
                  ["input"] = "work",
                  ["description"] = "d",
              });
          var child = new BlockingChatClient();
          InMemoryCallSessions sessions = new(BuildFactory(LingeringChildYaml, parent, child), TimeSpan.FromMinutes(30), TimeProvider.System);

          var session = await sessions.OpenAsync("call-1", token);
          await session.RunTurnAsync("go", token);

          // The turn is over and the call is not, and the child is still in its model call: the
          // leak this step closes. Nothing has cancelled it yet.
          await child.Started.WaitAsync(TimeSpan.FromSeconds(10), token);
          Assert.False(child.Cancelled.IsCompleted);

          await sessions.CloseAsync("call-1", token);

          await child.Cancelled.WaitAsync(TimeSpan.FromSeconds(10), token);
      }

      [Fact]
      public async Task ATurnThatReachesATerminalStage_CancelsAChildStillRunningWithoutCloseAsync()
      {
          var token = TestContext.Current.CancellationToken;
          var parent = new ToolCallingChatClient(
              "started",
              new Dictionary<string, object?>(StringComparer.Ordinal)
              {
                  ["agentName"] = "blocker",
                  ["input"] = "work",
                  ["description"] = "d",
              });
          var child = new BlockingChatClient();
          var factory = BuildFactory(TerminalChildYaml, parent, child);

          await using var session = factory.Create("call-1");
          var turn = await session.RunTurnAsync("go", token);

          Assert.True(turn.IsTerminal);
          await child.Started.WaitAsync(TimeSpan.FromSeconds(10), token);
          await child.Cancelled.WaitAsync(TimeSpan.FromSeconds(10), token);
      }

      private static CallSessionFactory BuildFactory(string yaml, IChatClient parent, IChatClient child)
      {
          var chatClients = new RoutingChatClientFactory(parent);
          chatClients.Route("parent", parent);
          chatClients.Route("blocker", child);

          var compiled = ConfigurationCompiler.CompileAll(
              ConfigurationLoader.LoadYaml(yaml), new AgentCompilationContext(chatClients))["main"];

          return new CallSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards));
      }

      /// <summary>A model that stays inside its first request until the run is cancelled.</summary>
      private sealed class BlockingChatClient : IChatClient
      {
          private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
          private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

          /// <summary>Completes when the child enters its model call.</summary>
          public Task Started => _started.Task;

          /// <summary>Completes when the child observes the cancel.</summary>
          public Task Cancelled => _cancelled.Task;

          public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
              IEnumerable<ChatMessage> messages,
              ChatOptions? options = null,
              [EnumeratorCancellation] CancellationToken cancellationToken = default)
          {
              ArgumentNullException.ThrowIfNull(messages);
              _started.TrySetResult();

              try
              {
                  await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
              }
              catch (OperationCanceledException)
              {
                  _cancelled.TrySetResult();
                  throw;
              }

              yield break;
          }

          public async Task<ChatResponse> GetResponseAsync(
              IEnumerable<ChatMessage> messages,
              ChatOptions? options = null,
              CancellationToken cancellationToken = default)
          {
              List<ChatResponseUpdate> updates = [];
              await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
                  .ConfigureAwait(false))
              {
                  updates.Add(update);
              }

              return updates.ToChatResponse();
          }

          public object? GetService(Type serviceType, object? serviceKey = null)
          {
              ArgumentNullException.ThrowIfNull(serviceType);
              return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
          }

          public void Dispose()
          {
              // Nothing to release.
          }
      }
  }
  #pragma warning restore MAAI001

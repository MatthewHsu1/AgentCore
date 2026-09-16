using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>The documents and session-building helpers a call's resume tests share.</summary>
internal static class CallSessionResumeTestSupport
{
    internal const string OneAgentYaml = """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            agent: only
        """;

    /// <summary>
    /// A document with something to forget: one slot a writer fills, and a stage that moves.
    /// </summary>
    internal const string StagedYaml = """
          apiVersion: agentcore/v1
          state:
            turnsTaken:
              type: integer
              default: 0
              writer: counter
              increment: { ">=": [ { var: turnIndex }, 0 ] }
          guards:
            pastFirstTurn: { ">": [ { var: turnIndex }, 0 ] }
          agents:
            items:
              - { id: intake, instructions: "ask the caller for the model" }
              - { id: help,   instructions: "help the caller" }
          entries:
            main:
              policy:
                initial: intake
                stages:
                  - { id: intake, agent: intake, to: [ { stage: help, when: pastFirstTurn } ] }
                  - { id: help,   agent: help }
          """;

      /// <summary>
      /// A document whose stages run three deep, so a machine left in the first one is visible.
      /// </summary>
      internal const string ThreeStageYaml = """
          apiVersion: agentcore/v1
          guards:
            always: { ">=": [ { var: turnIndex }, 0 ] }
          agents:
            items:
              - { id: first,  instructions: "greet the caller" }
              - { id: second, instructions: "ask the caller for the model" }
              - { id: third,  instructions: "help the caller" }
          entries:
            main:
              policy:
                initial: one
                stages:
                  - { id: one,   agent: first,  to: [ { stage: two,   when: always } ] }
                  - { id: two,   agent: second, to: [ { stage: three, when: always } ] }
                  - { id: three, agent: third }
          """;

      internal static async Task<string> FirstTurnAsync(
          ICallStore store, string said, string heard)
      {
          using ScriptedChatClient reply = new(heard);
          var session = CreateSession(OneAgentYaml, reply, store);

          await session.RunTurnAsync(said, TestContext.Current.CancellationToken);
          await session.FlushTranscriptAsync();

          return session.CallId;
      }

      internal static async Task SecondTurnAsync(
          ICallStore store, string callId, string said, string heard)
      {
          using ScriptedChatClient reply = new(heard);
          var session = CreateSession(OneAgentYaml, reply, store, callId);

          await session.RunTurnAsync(said, TestContext.Current.CancellationToken);
          await session.FlushTranscriptAsync();
      }

      internal static CallSession CreateSession(
          string yaml,
          IChatClient reply,
          ICallStore store,
          string? callId = null,
          ICallObserver? observer = null)
      {
          var document = ConfigurationLoader.LoadYaml(yaml);
          var chatClients = new FakeChatClientFactory(reply);
          var compiled = ConfigurationCompiler.CompileAll(
              document,
              new AgentCompilationContext(chatClients)
              {
                  CallStore = store,
                  Tools = TestToolRegistry.From(document, null, TestContext.Current.CancellationToken),
              })["main"];

          var factory = new CallSessionFactory(
              compiled,
              new GuardEvaluator(compiled.Configuration.Guards),
              extractor: null,
              observers: observer is null ? null : [observer]);

          return factory.Create(callId);
      }
  }

  /// <summary>Keeps every fact of the call, in the order the turn loop raised them.</summary>
  internal sealed class RecordingObserver : ICallObserver
  {
      private readonly Lock _gate = new();
      private readonly List<CallEvent> _events = [];

      /// <summary>Gets what the call raised, oldest first.</summary>
      public IReadOnlyList<CallEvent> Events
      {
          get
          {
              lock (_gate)
              {
                  return [.. _events];
              }
          }
      }

      public ValueTask OnCallEventAsync(CallEvent callEvent, CancellationToken cancellationToken)
      {
          lock (_gate)
          {
              _events.Add(callEvent);
          }

          return ValueTask.CompletedTask;
      }
  }

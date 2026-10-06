using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Conversation.Commands;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Binding;
using AgentCore.Application.Tools.Registry;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Tools
{
    /// <summary>
    /// Drives a real turn end to end through <see cref="ConversationSession"/>, with a bound tool that declares
    /// a <see cref="ToolCallScope"/> parameter, and reads what the runtime actually filled it with.
    /// </summary>
    public sealed class ToolCallScopeTests
    {
        private const string Yaml = """
          apiVersion: agentcore/v1
          tools:
            - { id: request_human, kind: binding, binds: RequestHuman, description: "Ask a human to take the conversation." }
          agents:
            items:
              - { id: only, instructions: "help the caller", tools: [ request_human ] }
          entries:
            main:
              policy:
                initial: handling
                stages:
                  - { id: handling, agent: only, to: [ { stage: waiting } ] }
                  - { id: waiting, agent: only, to: [ { stage: handling } ] }
          """;

        [Fact]
        public async Task ABoundTool_ReceivesTheRunningConversationsScope()
        {
            List<ToolCallScope> captured = [];
            ToolBindingRegistry bindings = new();
            _ = bindings.Register("RequestHuman", (string reason, ToolCallScope scope) => captured.Add(scope));
            ConversationSession session = await CreateAsync(bindings, "conversation-scope-1");

            _ = await session.RunTurnAsync("I need a person", TestContext.Current.CancellationToken);

            ToolCallScope first = Assert.Single(captured);
            Assert.Equal(session.ConversationId, first.ConversationId);
            Assert.Equal(0, first.TurnIndex);
            Assert.Equal("handling", first.Stage);

            captured.Clear();
            _ = await session.RunTurnAsync("still need a person", TestContext.Current.CancellationToken);

            ToolCallScope second = Assert.Single(captured);
            Assert.Equal(session.ConversationId, second.ConversationId);
            Assert.Equal(1, second.TurnIndex);
        }

        [Fact]
        public async Task ABoundToolAsksItsOwnConversationForAnActionThroughTheScope()
        {
            List<ChannelCommandResult> answers = [];
            ToolBindingRegistry bindings = new();
            _ = bindings.Register(
                "RequestHuman",
                (string reason, ToolCallScope scope) => answers.Add(scope.Channel.Send(new EndCommand(ConversationEndReason.TransferredToHuman))));
            ConversationSession session = await CreateAsync(bindings, "conversation-scope-2");

            _ = await session.RunTurnAsync("I need a person", TestContext.Current.CancellationToken);

            Assert.Equal(ChannelCommandResult.Scheduled, Assert.Single(answers));
            Assert.True(session.IsComplete);
        }

        private static async Task<ConversationSession> CreateAsync(ToolBindingRegistry bindings, string conversationId)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(Yaml);
            FakeChatClientFactory chatClients = new(new PerTurnToolCallingChatClient("connecting you now."));
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients)
                {
                    Tools = await ToolRegistryBuilder.BuildAsync(
                        [new BindingToolSource(bindings)],
                        new ToolSourceContext(document),
                        TestContext.Current.CancellationToken),
                })["main"];

            return new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards)).Create(conversationId);
        }

        /// <summary>
        /// Calls the one offered tool once per turn, rather than once per conversation: it looks only at what
        /// came after the caller's latest message, so a tool call answered in an earlier turn does not
        /// stop this one from calling the tool again.
        /// </summary>
        private sealed class PerTurnToolCallingChatClient(string reply) : IChatClient
        {
            private readonly string _reply = reply;
            private int _nextCallId;

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(messages);
                await Task.Yield();

                List<ChatMessage> transcript = [.. messages];
                int lastUserIndex = transcript.FindLastIndex(message => message.Role == ChatRole.User);
                bool answeredThisTurn = transcript
                    .Skip(lastUserIndex + 1)
                    .Any(message => message.Contents.OfType<FunctionResultContent>().Any());

                string responseId = Guid.NewGuid().ToString("N");

                if (!answeredThisTurn && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
                {
                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [
                            new FunctionCallContent(
                                  $"conversation_{_nextCallId++}",
                                  tool.Name,
                                  new Dictionary<string, object?>(StringComparer.Ordinal)
                                  {
                                      ["reason"] = "the caller wants a person",
                                  }),
                        ])
                    {
                        ResponseId = responseId,
                        MessageId = responseId,
                    };

                    yield break;
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, _reply)
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
            }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                List<ChatResponseUpdate> updates = [];
                await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
                    .ConfigureAwait(false))
                {
                    updates.Add(update);
                }

                return updates.ToChatResponse();
            }

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }
    }
}

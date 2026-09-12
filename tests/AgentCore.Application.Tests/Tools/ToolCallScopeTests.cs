using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Binding;
using AgentCore.Application.Tools.Registry;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// Drives a real turn end to end through <see cref="CallSession"/>, with a bound tool that declares
/// a <see cref="ToolCallScope"/> parameter, and reads what the runtime actually filled it with.
/// </summary>
public sealed class ToolCallScopeTests
{
    private const string Yaml = """
        apiVersion: agentcore/v1
        name: scope-check
        tools:
          - { id: request_human, kind: binding, binds: RequestHuman, description: "Ask a human to take the call." }
        policy:
          initial: handling
          stages:
            - { id: handling, agent: only, to: [ { stage: waiting } ] }
            - { id: waiting, agent: only, to: [ { stage: handling } ] }
        agents:
          items:
            - { id: only, instructions: "help the caller", tools: [ request_human ] }
        """;

    [Fact]
    public async Task ABoundTool_ReceivesTheRunningCallsScope()
    {
        List<ToolCallScope> captured = [];
        ToolBindingRegistry bindings = new();
        bindings.Register("RequestHuman", (string reason, ToolCallScope scope) => captured.Add(scope));

        var document = ConfigurationLoader.LoadYaml(Yaml);
        var chatClients = new FakeChatClientFactory(new PerTurnToolCallingChatClient("connecting you now."));
        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients)
            {
                Tools = await ToolRegistryBuilder.BuildAsync(
                    [new BindingToolSource(bindings)],
                    new ToolSourceContext(document),
                    TestContext.Current.CancellationToken),
            });

        var factory = new CallSessionFactory(
            compiled, new GuardEvaluator(compiled.Configuration.Guards));
        var session = factory.Create("call-scope-1");

        await session.RunTurnAsync("I need a person", TestContext.Current.CancellationToken);

        var first = Assert.Single(captured);
        Assert.Equal(session.CallId, first.CallId);
        Assert.Equal(0, first.TurnIndex);
        Assert.Equal("handling", first.Stage);

        captured.Clear();
        await session.RunTurnAsync("still need a person", TestContext.Current.CancellationToken);

        var second = Assert.Single(captured);
        Assert.Equal(session.CallId, second.CallId);
        Assert.Equal(1, second.TurnIndex);
    }

    /// <summary>
    /// Calls the one offered tool once per turn, rather than once per call: it looks only at what
    /// came after the caller's latest message, so a tool call answered in an earlier turn does not
    /// stop this one from calling the tool again.
    /// </summary>
    private sealed class PerTurnToolCallingChatClient : IChatClient
    {
        private readonly string _reply;
        private int _nextCallId;

        public PerTurnToolCallingChatClient(string reply) => _reply = reply;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await Task.Yield();

            var transcript = messages.ToList();
            var lastUserIndex = transcript.FindLastIndex(message => message.Role == ChatRole.User);
            var answeredThisTurn = transcript
                .Skip(lastUserIndex + 1)
                .Any(message => message.Contents.OfType<FunctionResultContent>().Any());

            var responseId = Guid.NewGuid().ToString("N");

            if (!answeredThisTurn && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            $"call_{_nextCallId++}",
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
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

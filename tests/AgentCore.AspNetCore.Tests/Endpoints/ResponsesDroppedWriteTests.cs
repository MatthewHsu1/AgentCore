using System.Net;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// A turn whose store 1 append failed once is still filed under its response id: no other session holds that id,
    /// and the next turn reads the store back before it runs. Only a turn the store refused is never filed
    /// (<see cref="ResponsesTurnConflictTests"/>).
    /// </summary>
    public sealed class ResponsesDroppedWriteTests
    {
        private const string Yaml =
            """
            apiVersion: agentcore/v1
            agents:
              defaults:
                model: { ref: reply }
              items:
                - { id: greeter, instructions: "greet the caller" }
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: reply }
              conversations: { kind: test }
            entries:
              main:
                agent: greeter
            """;

        [Fact]
        public async Task AResponseWhoseAppendDroppedOnce_CanBeChainedFrom()
        {
            DownStore store = new();
            using FragmentingChatClient reply = new("answer one", "answer two", "answer three");
            await using ResponsesHost host = await ResponsesHost.StartAsync(Yaml, reply, options => options.UseConversationStores(store));

            using HttpResponseMessage first = await host.PostAsync(/*lang=json,strict*/ """{ "stream": false, "input": "first" }""");
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            string firstId = (await ResponsesHost.ReadJsonAsync(first))["id"]!.GetValue<string>();

            store.Down = true;
            using HttpResponseMessage second = await host.PostAsync(
                $$"""{ "stream": false, "previous_response_id": "{{firstId}}", "input": "second" }""");
            store.Down = false;
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            string secondId = (await ResponsesHost.ReadJsonAsync(second))["id"]!.GetValue<string>();

            using HttpResponseMessage third = await host.PostAsync(
                $$"""{ "stream": false, "previous_response_id": "{{secondId}}", "input": "third" }""");

            Assert.Equal(HttpStatusCode.OK, third.StatusCode);
            JsonNode answer = await ResponsesHost.ReadJsonAsync(third);
            Assert.Contains("answer three", answer.OutputText(), StringComparison.Ordinal);
        }

        /// <summary>An in-memory store whose appends fail while <see cref="Down"/> is set. It is its own adapter, of kind <c>test</c>.</summary>
        private sealed class DownStore() : DelegatingConversationStore(new InMemoryConversationStore()), IConversationStoreAdapter
        {
            public volatile bool Down;

            public string Kind => "test";

            public ValueTask<IConversationStore> OpenAsync(
                VendorProviderConfiguration entry, ISecretResolverPort? secrets, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult<IConversationStore>(this);
            }

            public override ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
                string conversationId,
                IReadOnlyList<ConversationMessageDraft> messages,
                ConversationSessionState? state = null,
                CancellationToken cancellationToken = default)
            {
                return Down
                    ? throw new InvalidOperationException("the conversation store is down.")
                    : base.AppendAsync(conversationId, messages, state, cancellationToken);
            }
        }
    }
}

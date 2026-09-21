#pragma warning disable OPENAI001

using System.Net;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Secrets;
using AgentCore.Infrastructure.Llm.OpenCodeGo;
using AgentCore.Infrastructure.Tests.Fakes;
using AgentCore.Infrastructure.Tests.Tools;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Llm.OpenCodeGo
{
    /// <summary>
    /// <c>providers.llm[].reasoningEffort</c> on the OpenCode Go adapter, and why a document ever
    /// writes it.
    /// </summary>
    /// <remarks>
    /// OpenCode Go's own docs are silent on the wire field; a real request against the live
    /// endpoint (2026-09-21) confirmed <c>reasoning_effort</c> on the chat-completions body, honoured
    /// by at least one hosted model (reasoning token count moved with the level). These tests pin
    /// that shape offline; none of them reach the network.
    /// </remarks>
    public sealed class OpenCodeGoReasoningEffortTests
    {
        private const string ApiKey = "sk-test-not-a-real-key";

        private const string Answer =
            /*lang=json,strict*/
            """
        {
          "id": "gen-test",
          "object": "chat.completion",
          "created": 1790012523,
          "model": "kimi-k2.6",
          "choices": [
            {
              "index": 0,
              "finish_reason": "stop",
              "message": { "role": "assistant", "content": "OK" }
            }
          ]
        }
        """;

        private static CancellationToken Token => TestContext.Current.CancellationToken;

        [Theory]
        [InlineData("none")]
        [InlineData("minimal")]
        [InlineData("low")]
        [InlineData("medium")]
        [InlineData("high")]
        public async Task EveryValueTheSchemaAllows_ReachesTheRequestTheVendorSees(string effort)
        {
            CapturingChatClient inner = new();

            IChatClient client = OpenCodeGoChatClientAdapter.WithReasoningEffort(inner, effort);
            _ = await client.GetResponseAsync("hi", cancellationToken: Token);

            ChatCompletionOptions raw = Assert.IsType<ChatCompletionOptions>(
                inner.Seen!.RawRepresentationFactory!(inner));

            Assert.Equal(effort, raw.ReasoningEffortLevel.ToString());
        }

        [Fact]
        public async Task AnEntryThatNamesNoEffort_SendsNoReasoningValueAtAll()
        {
            CapturingChatClient inner = new();

            IChatClient client = OpenCodeGoChatClientAdapter.WithReasoningEffort(inner, effort: null);
            _ = await client.GetResponseAsync("hi", cancellationToken: Token);

            // No wrapper was applied at all: the caller's own client, unmodified, saw the request.
            Assert.Same(inner, client);
            Assert.Null(inner.Seen);
        }

        [Fact]
        public async Task RawOptionsTheCallerBuiltItself_KeepTheValueItAlreadyHolds()
        {
            CapturingChatClient inner = new();
            ChatCompletionOptions mine = new()
            {
                ReasoningEffortLevel = ChatReasoningEffortLevel.High,
                MaxOutputTokenCount = 42,
            };

            IChatClient client = OpenCodeGoChatClientAdapter.WithReasoningEffort(inner, "low");
            _ = await client.GetResponseAsync(
                "hi",
                new ChatOptions { RawRepresentationFactory = _ => mine },
                Token);

            Assert.Same(mine, inner.Seen!.RawRepresentationFactory!(inner));
            Assert.Equal(ChatReasoningEffortLevel.High, mine.ReasoningEffortLevel);
            Assert.Equal(42, mine.MaxOutputTokenCount);
        }

        [Fact]
        public async Task AnEntryWithAnEffort_PutsReasoningEffortOnTheRequestTheVendorSees()
        {
            string? body = null;
            StubHttpMessageHandler endpoint = new(request =>
            {
                body = request.Content!.ReadAsStringAsync(Token).GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Answer, System.Text.Encoding.UTF8, "application/json"),
                };
            });
            OpenCodeGoChatClientAdapter adapter = new(new StubHandlerFactory(endpoint));
            MapSecretResolver secrets = new MapSecretResolver()
                .With(OpenCodeGoChatClientAdapter.ApiKeySecretName, ApiKey);

            IChatClient client = await adapter.CreateClientAsync(
                new LlmProviderConfiguration
                {
                    Kind = OpenCodeGoChatClientAdapter.ProviderKind,
                    Model = "kimi-k2.6",
                    As = "reply",
                    ReasoningEffort = "high",
                },
                secrets,
                Token);

            _ = await client.GetResponseAsync(
                "hi",
                new ChatOptions
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        [Application.Llm.ChatRequestProperties.ConversationId] = "conv-1",
                    },
                },
                Token);

            Assert.Single(endpoint.Requests);
            Assert.Contains("\"reasoning_effort\":\"high\"", body, StringComparison.Ordinal);
        }

        [Fact]
        public void AValueThisVendorDoesNotKnow_FailsAndSaysWhatIsAllowed()
        {
            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(
                () => OpenCodeGoChatClientAdapter.WithReasoningEffort(new CapturingChatClient(), "exhaustive"));

            Assert.Contains(failure.Errors, error => error.Pointer == "/providers/llm");
            Assert.Contains("exhaustive", failure.Message, StringComparison.Ordinal);
            Assert.Contains("none", failure.Message, StringComparison.Ordinal);
        }
    }
}

#pragma warning disable OPENAI001

using System.Net;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Infrastructure.Llm.OpenCodeGo;
using AgentCore.Infrastructure.Tests.Fakes;
using AgentCore.Infrastructure.Tests.Tools;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Llm.OpenCodeGo
{
    /// <summary>
    /// <c>providers.llm[].reasoningEffort</c> on the OpenCode Go adapter, and why a document ever
    /// writes it.
    /// </summary>
    /// <remarks>
    /// OpenCode Go's own docs are silent on the wire field; a real request against the live
    /// endpoint (2026-09-21) confirmed <c>reasoning.effort</c> on the Responses body, honoured
    /// by at least one hosted model (reasoning token count moved with the level). These tests pin
    /// that shape offline; none of them reach the network.
    /// </remarks>
    public sealed class OpenCodeGoReasoningEffortTests
    {
        private const string ApiKey = "sk-test-not-a-real-key";

        /// <summary>A real Responses API body from opencode.ai (2026-09-21), trimmed to what the SDK reads.</summary>
        private const string Answer =
            /*lang=json,strict*/
            """
        {
          "id": "resp_6ab1bca48e6d09672c4f4283",
          "object": "response",
          "created_at": 1790033060,
          "status": "completed",
          "model": "muse-spark-1.3-contributor",
          "error": null,
          "incomplete_details": null,
          "output": [
            {
              "id": "msg_5e6ea904-c9e3-4047-b4eb-9836a61b656b",
              "type": "message",
              "status": "completed",
              "role": "assistant",
              "content": [
                { "type": "output_text", "text": "OK", "annotations": [], "logprobs": [] }
              ]
            }
          ],
          "usage": {
            "input_tokens": 12,
            "output_tokens": 148,
            "total_tokens": 160,
            "input_tokens_details": { "cached_tokens": 0 },
            "output_tokens_details": { "reasoning_tokens": 137 }
          }
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

            IChatClient client = OpenCodeGoChatClientAdapter.WithResponseDefaults(inner, effort);
            _ = await client.GetResponseAsync("hi", cancellationToken: Token);

            CreateResponseOptions raw = Assert.IsType<CreateResponseOptions>(
                inner.Seen!.RawRepresentationFactory!(inner));

            Assert.Equal(effort, raw.ReasoningOptions!.ReasoningEffortLevel.ToString());
        }

        [Fact]
        public async Task AnEntryThatNamesNoEffort_SendsNoReasoningValueButStillTurnsStoreOff()
        {
            CapturingChatClient inner = new();

            IChatClient client = OpenCodeGoChatClientAdapter.WithResponseDefaults(inner, effort: null);
            _ = await client.GetResponseAsync("hi", cancellationToken: Token);

            CreateResponseOptions raw = Assert.IsType<CreateResponseOptions>(
                inner.Seen!.RawRepresentationFactory!(inner));

            Assert.Null(raw.ReasoningOptions);
            Assert.False(raw.StoredOutputEnabled);
        }

        [Fact]
        public async Task RawOptionsTheCallerBuiltItself_KeepTheValueItAlreadyHolds()
        {
            CapturingChatClient inner = new();
            CreateResponseOptions mine = new()
            {
                ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.High },
                MaxOutputTokenCount = 42,
            };

            IChatClient client = OpenCodeGoChatClientAdapter.WithResponseDefaults(inner, "low");
            _ = await client.GetResponseAsync(
                "hi",
                new ChatOptions { RawRepresentationFactory = _ => mine },
                Token);

            Assert.Same(mine, inner.Seen!.RawRepresentationFactory!(inner));
            Assert.Equal(ResponseReasoningEffortLevel.High, mine.ReasoningOptions.ReasoningEffortLevel);
            Assert.Equal(42, mine.MaxOutputTokenCount);
        }

        [Fact]
        public async Task AnEntryWithAnEffort_PutsReasoningEffortAndStoreFalseOnTheRequestTheVendorSees()
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
                    Model = "muse-spark-1.3-contributor",
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

            _ = Assert.Single(endpoint.Requests);
            Assert.Contains("\"reasoning\":{\"effort\":\"high\"}", body, StringComparison.Ordinal);
            Assert.Contains("\"store\":false", body, StringComparison.Ordinal);
        }

        [Fact]
        public void AValueThisVendorDoesNotKnow_FailsAndSaysWhatIsAllowed()
        {
            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(
                () => OpenCodeGoChatClientAdapter.WithResponseDefaults(new CapturingChatClient(), "exhaustive"));

            Assert.Contains(failure.Errors, error => error.Pointer == "/providers/llm");
            Assert.Contains("exhaustive", failure.Message, StringComparison.Ordinal);
            Assert.Contains("none", failure.Message, StringComparison.Ordinal);
        }
    }
}

using System.Net;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Llm;
using AgentCore.Application.Secrets;
using AgentCore.Infrastructure.Llm.OpenCodeGo;
using AgentCore.Infrastructure.Tests.Fakes;
using AgentCore.Infrastructure.Tests.Tools;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Llm.OpenCodeGo
{
    /// <summary>
    /// The OpenCode Go adapter: an OpenAI-compatible endpoint that refuses any request missing its two
    /// vendor-specific headers.
    /// </summary>
    /// <remarks>
    /// Every test here reaches the network zero times, the same way <c>OpenAiChatClientAdapterTests</c>
    /// does: <see cref="StubHandlerFactory"/> stands in for the outbound pipeline, and
    /// <see cref="StubHttpMessageHandler"/> stands in for opencode.ai.
    /// </remarks>
    public sealed class OpenCodeGoChatClientAdapterTests
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

        [Fact]
        public void TheAdapter_ServesTheOpenCodeGoKind()
        {
            Assert.Equal("opencode-go", new OpenCodeGoChatClientAdapter(new StubHandlerFactory(
                StubHttpMessageHandler.Answering(HttpStatusCode.OK, Answer))).Kind);
        }

        [Fact]
        public async Task CreateClientAsync_OpensTheNamedClientOnThePipeline()
        {
            StubHandlerFactory pipeline = new(StubHttpMessageHandler.Answering(HttpStatusCode.OK, Answer));

            _ = await BuildClientAsync(pipeline);

            Assert.Equal(["agentcore.opencode-go"], pipeline.Names);
        }

        [Fact]
        public async Task NoApiKeyAnywhere_FailsAndSaysWhereToPutOne()
        {
            string? saved = Environment.GetEnvironmentVariable(OpenCodeGoChatClientAdapter.ApiKeyVariableName);
            Environment.SetEnvironmentVariable(OpenCodeGoChatClientAdapter.ApiKeyVariableName, null);

            try
            {
                OpenCodeGoChatClientAdapter adapter = new(new StubHandlerFactory(
                    StubHttpMessageHandler.Answering(HttpStatusCode.OK, Answer)));

                SecretResolutionException failure = await Assert.ThrowsAsync<SecretResolutionException>(
                    async () => await adapter.CreateClientAsync(Entry(), new MapSecretResolver(), Token));

                Assert.Contains(OpenCodeGoChatClientAdapter.ApiKeySecretName, failure.Message, StringComparison.Ordinal);
                Assert.Contains(OpenCodeGoChatClientAdapter.ApiKeyVariableName, failure.Message, StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable(OpenCodeGoChatClientAdapter.ApiKeyVariableName, saved);
            }
        }

        [Fact]
        public async Task TheApiKey_ResolvesOnceHoweverManyEntriesTheDocumentDeclares()
        {
            CountingSecretResolver resolver = new();
            OpenCodeGoChatClientAdapter adapter = new(new StubHandlerFactory(
                StubHttpMessageHandler.Answering(HttpStatusCode.OK, Answer)));

            _ = await adapter.CreateClientAsync(Entry("reply", "kimi-k2.6"), resolver, Token);
            _ = await adapter.CreateClientAsync(Entry("fill", "glm-5.3"), resolver, Token);

            Assert.Equal(1, resolver.Resolutions);
        }

        [Fact]
        public async Task EveryRequest_PostsToTheOpenCodeGoResponsesRoute()
        {
            StubHttpMessageHandler endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, Answer);
            IChatClient client = await BuildClientAsync(new StubHandlerFactory(endpoint));

            _ = await client.GetResponseAsync("hi", Options("conv-1"), Token);

            HttpRequestMessage request = Assert.Single(endpoint.Requests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://opencode.ai", request.RequestUri!.GetLeftPart(UriPartial.Authority));
            Assert.Equal("/zen/go/v1/responses", request.RequestUri.AbsolutePath);
        }

        [Fact]
        public async Task EveryRequest_CarriesTheSessionHeaderAndTheUserAgentOpenCodeGoAsksFor()
        {
            StubHttpMessageHandler endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, Answer);
            IChatClient client = await BuildClientAsync(new StubHandlerFactory(endpoint));

            _ = await client.GetResponseAsync("hi", Options("conv-1"), Token);

            HttpRequestMessage request = Assert.Single(endpoint.Requests);
            Assert.True(request.Headers.Contains(OpenCodeGoChatClientAdapter.SessionHeaderName));
            Assert.Equal(
                OpenCodeGoChatClientAdapter.UserAgentValue,
                request.Headers.UserAgent.ToString());
        }

        [Fact]
        public async Task TwoRequests_CarryTheSameSessionId()
        {
            StubHttpMessageHandler endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, Answer);
            IChatClient client = await BuildClientAsync(new StubHandlerFactory(endpoint));

            _ = await client.GetResponseAsync("first", Options("conv-1"), Token);
            _ = await client.GetResponseAsync("second", Options("conv-1"), Token);

            Assert.Equal(2, endpoint.Requests.Count);
            IEnumerable<string> first = endpoint.Requests[0].Headers.GetValues(OpenCodeGoChatClientAdapter.SessionHeaderName);
            IEnumerable<string> second = endpoint.Requests[1].Headers.GetValues(OpenCodeGoChatClientAdapter.SessionHeaderName);
            Assert.Equal(first, second);
        }

        [Fact]
        public async Task TwoConversations_CarryDifferentSessionIds()
        {
            StubHttpMessageHandler endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, Answer);
            IChatClient client = await BuildClientAsync(new StubHandlerFactory(endpoint));

            _ = await client.GetResponseAsync("hi", Options("conv-1"), Token);
            _ = await client.GetResponseAsync("hi", Options("conv-2"), Token);

            Assert.Equal(2, endpoint.Requests.Count);
            IEnumerable<string> first = endpoint.Requests[0].Headers.GetValues(OpenCodeGoChatClientAdapter.SessionHeaderName);
            IEnumerable<string> second = endpoint.Requests[1].Headers.GetValues(OpenCodeGoChatClientAdapter.SessionHeaderName);
            Assert.NotEqual(first, second);
        }

        [Fact]
        public async Task ARequestOutsideAnyConversation_Throws()
        {
            StubHttpMessageHandler endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, Answer);
            IChatClient client = await BuildClientAsync(new StubHandlerFactory(endpoint));

            _ = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await client.GetResponseAsync("hi", options: null, Token));

            Assert.Empty(endpoint.Requests);
        }

        [Fact]
        public async Task CreateClientAsync_OpensNoSocket()
        {
            StubHttpMessageHandler endpoint = StubHttpMessageHandler.Answering(HttpStatusCode.OK, Answer);

            _ = await BuildClientAsync(new StubHandlerFactory(endpoint));

            Assert.Empty(endpoint.Requests);
        }

        private static async Task<IChatClient> BuildClientAsync(StubHandlerFactory pipeline)
        {
            OpenCodeGoChatClientAdapter adapter = new(pipeline);
            MapSecretResolver resolver = new MapSecretResolver()
                .With(OpenCodeGoChatClientAdapter.ApiKeySecretName, ApiKey);

            return await adapter.CreateClientAsync(Entry(), resolver, Token);
        }

        private static ChatOptions Options(string conversationId)
        {
            return new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [ChatRequestProperties.ConversationId] = conversationId,
                },
            };
        }

        private static LlmProviderConfiguration Entry(string asName = "reply", string model = "kimi-k2.6")
        {
            return new()
            {
                Kind = OpenCodeGoChatClientAdapter.ProviderKind,
                Model = model,
                As = asName,
            };
        }

        /// <summary>A resolver that counts its reads, so a test proves the key resolves once.</summary>
        private sealed class CountingSecretResolver : Application.Ports.ISecretResolverPort
        {
            public int Resolutions { get; private set; }

            public ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
            {
                Resolutions++;
                return ValueTask.FromResult<string?>(ApiKey);
            }
        }
    }
}

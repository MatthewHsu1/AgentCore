using System.Net;
using System.Text;
using AgentCore.Application.Configuration.Schema;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice;
using AgentCore.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// A route with an <see cref="IEntrySelector"/> runs the entry the server picks, not one the URL names
    /// (entry selector spec, section 4).
    /// </summary>
    public sealed class EntrySelectorTests
    {
        private const string Route = "/v1/chat/responses";

        private const string TwoEntryYaml =
            """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: front, instructions: "answer visitors", model: { ref: front } }
                - { id: desk, instructions: "answer staff", model: { ref: desk } }
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: front }
                - { kind: openai, model: gpt-4.1-mini, as: desk }
            entries:
              main:
                agent: front
              staff:
                agent: desk
            """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact(Timeout = 60_000)]
        public async Task TheSelectorsEntryAnswers()
        {
            Models models = new();
            await using ResponsesHost host = await StartAsync(models, "staff");

            using HttpResponseMessage response = await PostAsync(host);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("desk reply", (await ResponsesHost.ReadJsonAsync(response)).OutputText(), StringComparison.Ordinal);
            Assert.Equal(0, models.Front.Calls);
        }

        [Fact(Timeout = 60_000)]
        public async Task ARefusingSelectorAnswers403AndRunsNoAgent()
        {
            Models models = new();
            await using ResponsesHost host = await StartAsync(models, entry: null);

            using HttpResponseMessage response = await PostAsync(host);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("entry_refused", (await ResponsesHost.ReadJsonAsync(response))["error"]!["code"]!.GetValue<string>());
            Assert.Equal(0, models.Front.Calls + models.Desk.Calls);
        }

        [Fact(Timeout = 60_000)]
        public async Task ASelectorThatNamesAnUndeclaredEntryAnswers404()
        {
            await using ResponsesHost host = await StartAsync(new Models(), "nobody");

            using HttpResponseMessage response = await PostAsync(host);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("unknown_entry", (await ResponsesHost.ReadJsonAsync(response))["error"]!["code"]!.GetValue<string>());
        }

        [Fact(Timeout = 60_000)]
        public async Task MiddlewareThatResolvesFirstLeavesTheSelectorRunOnce()
        {
            FixedSelector selector = new("staff");
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TwoEntryYaml,
                new FragmentingChatClient("unused"),
                new Models().Bind,
                services => services.AddSingleton(selector),
                app =>
                {
                    _ = app.Use(async (http, next) =>
                    {
                        _ = await AgentCoreEntries.ResolveAsync(http, http.RequestAborted);
                        await next(http);
                    });
                    _ = app.MapResponses(Route).SelectEntry<FixedSelector>();
                });

            using HttpResponseMessage response = await PostAsync(host);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, selector.Calls);
        }

        [Fact(Timeout = 60_000)]
        public async Task TheConversationRouteRefusesBeforeItsHandlerRuns()
        {
            HandlerProbe transport = new();
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                SocketYaml,
                new FragmentingChatClient("unused"),
                options =>
                {
                    _ = options.UseConversation(transport);
                    _ = options.UseSpeech(new SilentSpeech());
                },
                services => services.AddSingleton(new FixedSelector(null)),
                app => app.MapCall("/v1/call").SelectEntry<FixedSelector>());

            using HttpResponseMessage response = await host.Client.GetAsync(new Uri("/v1/call", UriKind.Relative), Ct);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(transport.Ran);
        }

        private const string SocketYaml =
            """
            apiVersion: agentcore/v1
            agents:
              items:
                - { id: front, instructions: "answer visitors" }
            providers:
              conversation:   { kind: probe }
              speech:
                stt: { kind: probe }
                tts: { kind: probe }
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: reply }
            entries:
              main:
                agent: front
            """;

        private static Task<ResponsesHost> StartAsync(Models models, string? entry)
        {
            return ResponsesHost.StartAsync(
                TwoEntryYaml,
                new FragmentingChatClient("unused"),
                models.Bind,
                services => services.AddSingleton(new FixedSelector(entry)),
                app => app.MapResponses(Route).SelectEntry<FixedSelector>());
        }

        private static Task<HttpResponseMessage> PostAsync(ResponsesHost host)
        {
            HttpRequestMessage request = new(HttpMethod.Post, Route)
            {
                Content = new StringContent(/*lang=json,strict*/ """{ "stream": false, "input": "hi" }""", Encoding.UTF8, "application/json"),
            };

            return host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        }

        /// <summary>One model per agent, so a test reads which agent answered.</summary>
        private sealed class Models
        {
            public FragmentingChatClient Front { get; } = new("front reply");

            public FragmentingChatClient Desk { get; } = new("desk reply");

            public void Bind(AgentCore.AspNetCore.DependencyInjection.AgentCoreOptions options)
            {
                _ = options.UseChatClients(_ => new RoutingChatClientFactory().Route("front", Front).Route("desk", Desk));
            }
        }

        /// <summary>Answers one entry for every caller, and counts how often it was asked.</summary>
        private sealed class FixedSelector(string? entry) : IEntrySelector
        {
            private int _calls;

            public int Calls => Volatile.Read(ref _calls);

            public ValueTask<string?> SelectAsync(HttpContext http, CancellationToken cancellationToken)
            {
                _ = Interlocked.Increment(ref _calls);
                return ValueTask.FromResult(entry);
            }
        }

        /// <summary>A transport whose handler records that it ran. The handler is where a socket upgrade happens.</summary>
        private sealed class HandlerProbe : IConversationTransportAdapter
        {
            public string Kind => "probe";

            public bool CarriesText => true;

            public bool Ran { get; private set; }

            public RequestDelegate CreateHandler(ConversationProviderConfiguration configuration)
            {
                return _ =>
                {
                    Ran = true;
                    return Task.CompletedTask;
                };
            }
        }

        /// <summary>The speech vendor the pairing rule reads, which builds nothing.</summary>
        private sealed class SilentSpeech : ISpeechAdapter
        {
            public string Kind => "probe";
        }
    }
}

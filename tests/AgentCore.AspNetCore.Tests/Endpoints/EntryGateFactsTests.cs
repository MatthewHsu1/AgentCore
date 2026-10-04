using System.Net;
using System.Security.Claims;
using System.Text;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Hooks.Gates;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice.Ports;
using AgentCore.AspNetCore.Voice.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;
using static AgentCore.AspNetCore.Tests.Endpoints.EntryGateFixture;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>What the entry gate sees: the route, the URL's entry, the headers, the caller and the transport.</summary>
    public sealed class EntryGateFactsTests
    {
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

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact(Timeout = 60_000)]
        public async Task TheGateSeesTheRouteTheUrlsEntryTheHeadersAndTheTransport()
        {
            List<EntryGate> seen = [];
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TwoEntryYaml,
                new FragmentingChatClient("unused"),
                options =>
                {
                    new Models().Bind(options);
                    _ = options.UseHooks(new Entry(seen.Add));
                },
                map: app =>
                {
                    // HttpClient folds repeated values into one "a, b" line, so the second value is set server-side.
                    _ = app.Use((http, next) =>
                    {
                        http.Request.Headers["X-Caller"] = new StringValues(["a", "b"]);
                        return next(http);
                    });
                    _ = app.MapResponses("/v1/{entry}/responses");
                });

            using HttpRequestMessage request = new(HttpMethod.Post, "/v1/staff/responses")
            {
                Content = new StringContent(/*lang=json,strict*/ """{ "stream": false, "input": "hi" }""", Encoding.UTF8, "application/json"),
            };
            using HttpResponseMessage response = await host.Client.SendAsync(request, Ct);

            EntryGate gate = Assert.Single(seen);
            Assert.Equal(("/v1/{entry}/responses", "staff", EntryTransport.Http), (gate.Route, gate.RequestedEntry, gate.Transport));
            Assert.Equal("a,b", gate.Headers["X-Caller"]);
            Assert.Null(gate.User);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact(Timeout = 60_000)]
        public async Task AnAuthenticatedCallersPrincipalReachesTheGateWithItsClaims()
        {
            List<EntryGate> seen = [];
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TwoEntryYaml,
                new FragmentingChatClient("unused"),
                options =>
                {
                    new Models().Bind(options);
                    _ = options.UseHooks(new Entry(seen.Add));
                },
                map: app =>
                {
                    _ = app.Use((http, next) =>
                    {
                        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "ada")], authenticationType: "test"));
                        return next(http);
                    });
                    _ = app.MapResponses("/v1/{entry}/responses");
                });

            using HttpRequestMessage request = new(HttpMethod.Post, "/v1/staff/responses")
            {
                Content = new StringContent(/*lang=json,strict*/ """{ "stream": false, "input": "hi" }""", Encoding.UTF8, "application/json"),
            };
            using HttpResponseMessage response = await host.Client.SendAsync(request, Ct);

            EntryGate gate = Assert.Single(seen);
            Assert.Equal("ada", gate.User?.Identity?.Name);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact(Timeout = 60_000)]
        public async Task TheGatesHeadersCannotBeChanged()
        {
            Exception? thrown = null;
            Models models = new();
            await using ResponsesHost host = await StartAsync(
                models,
                new Entry(gate => thrown = Record.Exception(() => ((IDictionary<string, string>)gate.Headers)["X-Added"] = "1")));

            using HttpResponseMessage response = await PostAsync(host);

            Assert.IsType<NotSupportedException>(thrown);
        }

        [Fact(Timeout = 60_000)]
        public async Task TheConversationRouteRefusesBeforeItsHandlerRunsAndSaysItIsACall()
        {
            HandlerProbe transport = new();
            List<EntryTransport> transports = [];
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                SocketYaml,
                new FragmentingChatClient("unused"),
                options =>
                {
                    _ = options.UseConversation(transport);
                    _ = options.UseSpeech(new SilentSpeech());
                    _ = options.UseHooks(new Entry(gate => { transports.Add(gate.Transport); gate.Refuse(); }));
                },
                map: app => app.MapCall("/v1/call"));

            using HttpResponseMessage response = await host.Client.GetAsync(new Uri("/v1/call", UriKind.Relative), Ct);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(transport.Ran);
            Assert.Equal([EntryTransport.Call], transports);
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

using System.Net;
using System.Text;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Endpoints;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Xunit;
using static AgentCore.AspNetCore.Tests.Endpoints.EntryGateFixture;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>The entry gate picks the entry a request runs; the URL's entry is the default.</summary>
    public sealed class EntryGateTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact(Timeout = 60_000)]
        public async Task TheHooksEntryAnswers()
        {
            Models models = new();
            await using ResponsesHost host = await StartAsync(models, new Entry(gate => gate.Choose("staff")));

            using HttpResponseMessage response = await PostAsync(host);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("desk reply", (await ResponsesHost.ReadJsonAsync(response)).OutputText(), StringComparison.Ordinal);
            Assert.Equal(0, models.Front.Calls);
        }

        [Fact(Timeout = 60_000)]
        public async Task ARefusingHookAnswers403AndRunsNoAgent()
        {
            Models models = new();
            await using ResponsesHost host = await StartAsync(models, new Entry(gate => gate.Refuse()));

            using HttpResponseMessage response = await PostAsync(host);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("entry_refused", (await ResponsesHost.ReadJsonAsync(response))["error"]!["code"]!.GetValue<string>());
            Assert.Equal(0, models.Front.Calls + models.Desk.Calls);
        }

        [Fact(Timeout = 60_000)]
        public async Task AHookThatNamesAnUndeclaredEntryAnswers404()
        {
            await using ResponsesHost host = await StartAsync(new Models(), new Entry(gate => gate.Choose("nobody")));

            using HttpResponseMessage response = await PostAsync(host);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("unknown_entry", (await ResponsesHost.ReadJsonAsync(response))["error"]!["code"]!.GetValue<string>());
        }

        [Fact(Timeout = 60_000)]
        public async Task WithNoHookTheUrlsEntryRuns()
        {
            Models models = new();
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TwoEntryYaml,
                new FragmentingChatClient("unused"),
                models.Bind,
                map: app => app.MapResponses("/v1/{entry}/responses"));

            using HttpRequestMessage request = new(HttpMethod.Post, "/v1/staff/responses")
            {
                Content = new StringContent(/*lang=json,strict*/ """{ "stream": false, "input": "hi" }""", Encoding.UTF8, "application/json"),
            };
            using HttpResponseMessage response = await host.Client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("desk reply", (await ResponsesHost.ReadJsonAsync(response)).OutputText(), StringComparison.Ordinal);
        }

        [Fact(Timeout = 60_000)]
        public async Task AHookThatDoesNotDecideTakesTheUrlsEntry()
        {
            Models models = new();
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TwoEntryYaml,
                new FragmentingChatClient("unused"),
                options =>
                {
                    models.Bind(options);
                    _ = options.UseHooks(new Entry(_ => { }));
                },
                map: app => app.MapResponses("/v1/{entry}/responses"));

            using HttpRequestMessage request = new(HttpMethod.Post, "/v1/staff/responses")
            {
                Content = new StringContent(/*lang=json,strict*/ """{ "stream": false, "input": "hi" }""", Encoding.UTF8, "application/json"),
            };
            using HttpResponseMessage response = await host.Client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("desk reply", (await ResponsesHost.ReadJsonAsync(response)).OutputText(), StringComparison.Ordinal);
        }

        [Fact(Timeout = 60_000)]
        public async Task ARouteWithoutAnEntrySegmentRefusesWhenTheHookDoesNotDecide()
        {
            Models models = new();
            await using ResponsesHost host = await StartAsync(models, new Entry(_ => { }));

            using HttpResponseMessage response = await PostAsync(host);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("entry_refused", (await ResponsesHost.ReadJsonAsync(response))["error"]!["code"]!.GetValue<string>());
            Assert.Equal(0, models.Front.Calls + models.Desk.Calls);
        }

        // A failing gate refuses the request.
        [Fact(Timeout = 60_000)]
        public async Task AHookThatThrowsIsRefusedEvenWhenTheUrlNamesAnEntry()
        {
            Models models = new();
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TwoEntryYaml,
                new FragmentingChatClient("unused"),
                options =>
                {
                    models.Bind(options);
                    _ = options.UseHooks(new Entry(_ => throw new InvalidOperationException("auth down")));
                },
                map: app => app.MapResponses("/v1/{entry}/responses"));

            using HttpRequestMessage request = new(HttpMethod.Post, "/v1/staff/responses")
            {
                Content = new StringContent(/*lang=json,strict*/ """{ "stream": false, "input": "hi" }""", Encoding.UTF8, "application/json"),
            };
            using HttpResponseMessage response = await host.Client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, models.Front.Calls + models.Desk.Calls);
        }

        [Fact(Timeout = 60_000)]
        public async Task ARefusalIsKeptSoTheGateRunsOnceWhenMiddlewareResolvesFirst()
        {
            Entry hook = new(gate => gate.Refuse());
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TwoEntryYaml,
                new FragmentingChatClient("unused"),
                options =>
                {
                    new Models().Bind(options);
                    _ = options.UseHooks(hook);
                },
                map: app =>
                {
                    _ = app.Use(async (http, next) =>
                    {
                        _ = await AgentCoreEntries.ResolveAsync(http, http.RequestAborted);
                        await next(http);
                    });
                    _ = app.MapResponses(Route);
                });

            using HttpResponseMessage response = await PostAsync(host);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(1, hook.Calls);
        }

        [Fact(Timeout = 60_000)]
        public async Task MiddlewareThatResolvesFirstLeavesTheGateRunOnce()
        {
            Entry hook = new(gate => gate.Choose("staff"));
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TwoEntryYaml,
                new FragmentingChatClient("unused"),
                options =>
                {
                    new Models().Bind(options);
                    _ = options.UseHooks(hook);
                },
                map: app =>
                {
                    _ = app.Use(async (http, next) =>
                    {
                        _ = await AgentCoreEntries.ResolveAsync(http, http.RequestAborted);
                        await next(http);
                    });
                    _ = app.MapResponses(Route);
                });

            using HttpResponseMessage response = await PostAsync(host);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, hook.Calls);
        }
    }
}

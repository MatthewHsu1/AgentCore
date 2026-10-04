using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.Hosting.Tests
{
    /// <summary>
    /// The routes <see cref="AgentCoreHostEndpointExtensions.MapAgentCoreHost"/> maps. The registrations answer
    /// nothing on their own.
    /// </summary>
    public sealed class AgentCoreHostRouteTests
    {
        [Fact]
        public async Task HealthAnswersOnTheMappedRoute()
        {
            await using WebApplication app = await StartMappedAsync();
            using HttpClient client = new() { BaseAddress = Address(app) };

            HttpResponseMessage response = await client.GetAsync(
                AgentCoreHostEndpointExtensions.HealthPattern,
                TestContext.Current.CancellationToken);

            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        /// <summary>The default Responses route, with the document's one entry filled in.</summary>
        private const string MainResponses = "/v1/main/responses";

        [Fact]
        public async Task ResponsesAnswersOnTheDefaultRoute()
        {
            await using WebApplication app = await StartMappedAsync();
            using HttpClient client = new() { BaseAddress = Address(app) };

            // No user message is a caller mistake this endpoint names, and naming it proves the route
            // reached the endpoint rather than the 404 handler.
            HttpResponseMessage response = await PostEmptyAsync(client, MainResponses);

            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task ResponsesRefusesAnEntryTheDocumentDoesNotDeclare()
        {
            await using WebApplication app = await StartMappedAsync();
            using HttpClient client = new() { BaseAddress = Address(app) };

            HttpResponseMessage response = await PostEmptyAsync(client, "/v1/nobody/responses");

            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("\"code\":\"unknown_entry\"", body);
            Assert.Contains("Valid entries: main", body);
        }

        [Fact]
        public async Task ResponsesMovesWhenTheHostNamesAnotherRoute()
        {
            // A host that mounts a second Responses surface of its own needs this one out of the
            // way, and it must actually leave the default route behind when it moves.
            await using WebApplication app = await StartMappedAsync("/agentcore/v1/{entry}/responses");
            using HttpClient client = new() { BaseAddress = Address(app) };

            Assert.Equal(
                System.Net.HttpStatusCode.BadRequest,
                (await PostEmptyAsync(client, "/agentcore/v1/main/responses")).StatusCode);
            Assert.Equal(
                System.Net.HttpStatusCode.NotFound,
                (await PostEmptyAsync(client, MainResponses)).StatusCode);
        }

        [Fact]
        public async Task ConversationRefusesAnEntryTheDocumentDoesNotDeclare()
        {
            await using WebApplication app = await StartMappedAsync();
            using HttpClient client = new() { BaseAddress = Address(app) };

            HttpResponseMessage response = await client.GetAsync("/v1/nobody/call", TestContext.Current.CancellationToken);

            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains(
                "Valid entries: main",
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task AHookPicksTheEntryOnARouteThatNamesNone()
        {
            await using WebApplication app = await HostingTestHost.BuildAsync(configure: options => options.UseHooks(new MainEntry()));
            _ = app.MapAgentCoreHost("/v1/chat/responses");
            await app.StartAsync(TestContext.Current.CancellationToken);
            using HttpClient client = new() { BaseAddress = Address(app) };

            // 400 names the missing user message, so the request reached the endpoint and ran "main".
            Assert.Equal(
                System.Net.HttpStatusCode.BadRequest,
                (await PostEmptyAsync(client, "/v1/chat/responses")).StatusCode);
        }

        [Fact]
        public async Task ARouteWithNoEntryParameterAndNoEntryHookFailsTheStart()
        {
            await using WebApplication app = await HostingTestHost.BuildAsync();
            _ = app.MapAgentCoreHost("/v1/chat/responses");

            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                () => app.StartAsync(TestContext.Current.CancellationToken));

            Assert.Contains("/v1/chat/responses", failure.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(AgentHook.BeforeEntryAsync), failure.Message, StringComparison.Ordinal);
        }

        /// <summary>Builds a host, maps every route, and puts it on a real socket.</summary>
        /// <param name="responsesPattern">The route the Responses endpoint answers on, or null for the default.</param>
        /// <returns>The started host.</returns>
        private static async Task<WebApplication> StartMappedAsync(string? responsesPattern = null)
        {
            WebApplication app = await HostingTestHost.BuildAsync();
            _ = app.MapAgentCoreHost(responsesPattern);
            await app.StartAsync(TestContext.Current.CancellationToken);
            return app;
        }

        /// <summary>Reads the address Kestrel took, since the tests ask for port zero.</summary>
        /// <param name="app">The started host.</param>
        /// <returns>The base address.</returns>
        private static Uri Address(WebApplication app)
        {
            return new(app.Services
                        .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                        .Features
                        .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                        .Addresses
                        .First(), UriKind.Absolute);
        }

        /// <summary>Posts a request with no user message, which every mapped route answers 400 to.</summary>
        /// <param name="client">The client that speaks to the host.</param>
        /// <param name="route">The route to post to.</param>
        /// <returns>The answer.</returns>
        private static Task<HttpResponseMessage> PostEmptyAsync(HttpClient client, string route)
        {
            return client.PostAsync(
                        route,
                        new StringContent(/*lang=json,strict*/ "{\"input\":[]}", System.Text.Encoding.UTF8, "application/json"),
                        TestContext.Current.CancellationToken);
        }


        /// <summary>Picks the document's one entry for every caller.</summary>
        private sealed class MainEntry : AgentHook
        {
            public override ValueTask BeforeEntryAsync(EntryGate gate, CancellationToken cancellationToken)
            {
                gate.Choose("main");
                return default;
            }
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Hooks;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Vendors.OpenAiLive;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using AgentCore.AspNetCore.Voice.Routing;
using AgentCore.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// One real host on Kestrel with the openai-live vendor behind <c>app.MapCall()</c>, a recording
    /// call-control API, and fake sidebands. No network, no OpenAI account.
    /// </summary>
    internal sealed class OpenAiLiveHost : IAsyncDisposable
    {
        /// <summary>The Standard Webhooks spec's example secret.</summary>
        public const string WebhookSecret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";

        public const string ApiKey = "sk-test";

        public const string MainCall = "/v1/main/call";

        public const string LiveYaml = """
        apiVersion: agentcore/v1
        agents:
          defaults:
            clock: false
            model: { ref: reply }
          items:
            - { id: only, instructions: "answer the caller" }
        providers:
          conversation:
            kind: openai-live
            live:
              instructions: "Be brief."
              greeting: "Say hello."
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        entries:
          main:
            agent: only
        """;

        private readonly WebApplication _app;
        private readonly HttpClient _client;
        private readonly AgentCoreHttpClients _pipeline;

        private OpenAiLiveHost(WebApplication app, HttpClient client, AgentCoreHttpClients pipeline, RecordingLiveControl control, List<FakeSideband> attached)
        {
            _app = app;
            _client = client;
            _pipeline = pipeline;
            Control = control;
            Attached = attached;
        }

        public RecordingLiveControl Control { get; }

        /// <summary>Gets every sideband the adapter attached, in order.</summary>
        public List<FakeSideband> Attached { get; }

        public TaskCompletionSource<FakeSideband> FirstAttach { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IServiceProvider Services => _app.Services;

        /// <summary>Starts the host.</summary>
        /// <param name="attachFault">Names the fault the sideband attach of a call throws, or <see langword="null"/> for none.</param>
        /// <param name="configure">Anything else the test binds on the options, such as a conversation store.</param>
        /// <param name="attachHeldUntil">Holds each sideband attach until it completes, or <see langword="null"/> to attach at once.</param>
        public static async Task<OpenAiLiveHost> StartAsync(
            IChatClient model,
            IReadOnlyList<AgentHook> hooks,
            MapSecretResolver? secrets = null,
            string yaml = LiveYaml,
            Func<LiveAttach, Exception?>? attachFault = null,
            Action<AgentCoreOptions>? configure = null,
            Task? attachHeldUntil = null)
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            _ = builder.WebHost.UseUrls("http://127.0.0.1:0");
            _ = builder.Logging.ClearProviders();

            RecordingLiveControl control = new();
            AgentCoreHttpClients pipeline = new(control);
            List<FakeSideband> attached = [];
            OpenAiLiveHost? host = null;
            OpenAiLiveConversationAdapter adapter = new(
                pipeline,
                secrets ?? new MapSecretResolver().With("openai-api-key", ApiKey).With("openai-webhook-secret", WebhookSecret),
                async (attach, token) =>
                {
                    if (attachFault?.Invoke(attach) is { } fault)
                    {
                        throw fault;
                    }

                    if (attachHeldUntil is not null)
                    {
                        await attachHeldUntil.WaitAsync(token);
                    }

                    // A real sideband opens with session.started (docs/probes/live-transfer-t1/t1-none.log, 0 ms after attach).
                    FakeSideband sideband = new();
                    sideband.Push([RunningLiveCall.Started]);
                    lock (attached)
                    {
                        attached.Add(sideband);
                    }

                    _ = host?.FirstAttach.TrySetResult(sideband);
                    return sideband;
                },
                new Uri("https://api.openai.test/"));

            _ = builder.Services.AddAgentCore(options =>
            {
                options.Configuration = ConfigurationLoader.LoadYaml(yaml);
                _ = options.UseChatClients(_ => new RoutingChatClientFactory(model));
                _ = options.UseConversation(adapter);
                _ = options.UseHooks([.. hooks]);
                configure?.Invoke(options);
            });

            WebApplication app = builder.Build();
            _ = app.MapCall();
            try
            {
                await app.StartAsync(TestContext.Current.CancellationToken);
            }
            catch
            {
                await app.DisposeAsync();
                pipeline.Dispose();
                throw;
            }

            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            host = new OpenAiLiveHost(app, new HttpClient { BaseAddress = new Uri(address) }, pipeline, control, attached);
            return host;
        }

        /// <summary>Posts one webhook, signed the Standard Webhooks way with an implementation of the test's own.</summary>
        public Task<HttpResponseMessage> PostWebhookAsync(string body, string webhookId = "wh_1", DateTimeOffset? signedAt = null, string? secret = null)
        {
            string timestamp = (signedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            byte[] key = Convert.FromBase64String((secret ?? WebhookSecret)["whsec_".Length..]);
            string signature = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{webhookId}.{timestamp}.{body}")));

            HttpRequestMessage request = new(HttpMethod.Post, MainCall) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("webhook-id", webhookId);
            request.Headers.Add("webhook-timestamp", timestamp);
            request.Headers.Add("webhook-signature", "v1," + signature);
            return _client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public static string IncomingCall(string callId)
        {
            return $$$"""{"object":"event","id":"evt_{{{callId}}}","type":"realtime.call.incoming","created_at":1790500000,"data":{"call_id":"{{{callId}}}","sip_headers":[{"name":"From","value":"<sip:+15550100@sip.telnyx.com>"},{"name":"To","value":"<sip:+15550199@sip.api.openai.com>"}]}}""";
        }

        public Task StopAsync()
        {
            return _app.StopAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            lock (Attached)
            {
                Attached.ForEach(sideband => sideband.Complete());
            }

            _client.Dispose();
            await _app.DisposeAsync();
            _pipeline.Dispose();
        }
    }
}

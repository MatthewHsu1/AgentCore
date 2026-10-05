using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Secrets;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Hosting.Tests
{
    /// <summary>The one host every test in this project builds, over a fake model and no key.</summary>
    internal static class HostingTestHost
    {
        /// <summary>The shared key the document's Telnyx relay checks callers with: without one the host does not start.</summary>
        internal const string RelayKey = "test-relay-key";

        /// <summary>The smallest document the schema accepts: one agent, one model, and the required pair.</summary>
        internal const string Document = """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        entries:
          main:
            agent: only
        providers:
          conversation:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: fake, model: fake-model, as: reply }
        """;

        /// <summary>Builds a host the way a deployable does, over a fake model and no key.</summary>
        /// <param name="configure">Anything else the test says on the options.</param>
        /// <param name="providers">A further line under <c>providers</c>, or null for the document above.</param>
        /// <param name="services">Anything else the test registers, such as an entry selector.</param>
        /// <returns>The built host, not started.</returns>
        internal static async Task<WebApplication> BuildAsync(
            Action<AgentCoreOptions>? configure = null,
            string? providers = null,
            Action<IServiceCollection>? services = null)
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            _ = builder.WebHost.UseUrls("http://127.0.0.1:0");
            _ = builder.Logging.ClearProviders();

            string document = providers is null ? Document : Document + Environment.NewLine + providers;

            _ = builder.AddAgentCoreHost(options =>
            {
                options.Configuration = ConfigurationLoader.LoadYaml(document);
                options.SecretResolver = new MapSecretResolver().With(KnownSecrets.TelnyxRelayKeyName, RelayKey);
                _ = options.UseChatClients(_ => new RecordingChatClientFactory(new FakeChatClient()));
                configure?.Invoke(options);
            });

            services?.Invoke(builder.Services);

            return builder.Build();
        }
    }
}

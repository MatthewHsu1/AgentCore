using AgentCore.Application.Configuration.Parsing;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentCore.AspNetCore.Tests.DependencyInjection
{
    /// <summary>Builds a host over one document, which is where the whole boot happens.</summary>
    internal static class StartedHostFixture
    {
        /// <summary>The one-agent document every test that needs no more than that boots on.</summary>
        internal const string OneAgentYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          conversation:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        entries:
          main:
            agent: only
        """;

        /// <summary>The conversation and speech providers the test documents share.</summary>
        internal const string SpeechAndConversation =
            """
        providers:
          conversation:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
        """;

        /// <summary><see cref="SpeechAndConversation"/> plus the single reply model most documents declare.</summary>
        internal const string MinimalProviders =
            $$"""
        {{SpeechAndConversation}}
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

        /// <summary>Starts a host on one document.</summary>
        /// <param name="yaml">The document to boot.</param>
        /// <param name="configure">The host's own word on the options.</param>
        /// <returns>The started host, read as the container it is.</returns>
        internal static async Task<StartedHost> BuildAsync(string yaml, Action<AgentCoreOptions>? configure = null)
        {
            HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
            ConfigureServices(builder.Services, yaml, configure);

            return await StartAsync(builder.Build());
        }

        /// <summary>Starts one host, and closes it itself when the start fails.</summary>
        /// <param name="host">The host to start.</param>
        /// <returns>The started host.</returns>
        internal static async Task<StartedHost> StartAsync(IHost host)
        {
            try
            {
                await host.StartAsync(TestContext.Current.CancellationToken);
            }
            catch
            {
                host.Dispose();
                throw;
            }

            return new StartedHost(host);
        }

        /// <summary>Starts a host on nothing but the options a test writes, with no document default.</summary>
        /// <param name="configure">The only word on the options.</param>
        /// <returns>The started host, for the tests that expect it never to get one.</returns>
        internal static async Task<StartedHost> StartBareAsync(Action<AgentCoreOptions> configure)
        {
            HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());
            _ = builder.Services.AddAgentCore(configure);

            return await StartAsync(builder.Build());
        }

        internal static void ConfigureServices(
            IServiceCollection services, string yaml, Action<AgentCoreOptions>? configure)
        {
            _ = services.AddAgentCore(options =>
                    {
                        options.Configuration = ConfigurationLoader.LoadYaml(yaml);
                        _ = options.UseChatClients(_ => new RoutingChatClientFactory(new FragmentingChatClient("hello")));
                        configure?.Invoke(options);
                    });
        }
    }
}

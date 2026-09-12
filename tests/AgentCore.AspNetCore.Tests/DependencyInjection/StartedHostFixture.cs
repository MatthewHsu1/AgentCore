using AgentCore.Application.Configuration.Parsing;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentCore.AspNetCore.Tests.DependencyInjection;

/// <summary>Builds a host over one document, which is where the whole boot happens.</summary>
/// <remarks>
/// Shared by every DI test that only needs a booted host and nothing document-specific — the
/// tests that need their own document still write their own YAML and call
/// <see cref="ConfigureServices"/> or <see cref="BuildAsync"/> directly.
/// </remarks>
internal static class StartedHostFixture
{
    /// <summary>The one-agent document every test that needs no more than that boots on.</summary>
    internal const string OneAgentYaml =
        """
        apiVersion: agentcore/v1
        name: composed
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
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
    /// <remarks>
    /// A failed start never stops what already started, so disposal is the only cleanup path — and
    /// it is the one a real host takes too, inside <c>RunAsync</c>'s own finally. StopAsync is
    /// deliberately not called here: on net10 a host that failed to start throws
    /// <see cref="ArgumentNullException"/> out of StopAsync when the failure was a constructor.
    /// </remarks>
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
        builder.Services.AddAgentCore(configure);

        return await StartAsync(builder.Build());
    }

    internal static void ConfigureServices(
        IServiceCollection services, string yaml, Action<AgentCoreOptions>? configure)
        => services.AddAgentCore(options =>
        {
            options.Configuration = ConfigurationLoader.LoadYaml(yaml);
            options.UseChatClients(_ => new RoutingChatClientFactory(new FragmentingChatClient("hello")));
            configure?.Invoke(options);
        });
}

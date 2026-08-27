using System.Net;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Secrets;
using AgentCore.Infrastructure.Tools.Mcp;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Tools;

/// <summary>
/// Where a credential actually goes, and whose HTTP pipeline actually carries it.
/// </summary>
/// <remarks>
/// A credential has to survive the whole way to the far side: onto a request header, or into a child
/// process's environment. These drive the real transports and a real child process, so nothing here
/// is a stub of the wire.
/// </remarks>
public sealed class McpConnectionFactoryTests
{
    private const string TokenValue = "ghp_live_0123456789";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------
    // stdio: the credential reaches the child's environment, resolved.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AStdioServersEnvironment_ReachesTheChildProcess_WithItsSecretResolved()
    {
        var written = Path.Combine(Path.GetTempPath(), $"mcp-env-{Guid.NewGuid():N}");
        try
        {
            // The child writes what it was given and then waits, so the handshake times out rather
            // than the process vanishing before it has been observed.
            await ConnectAndGiveUpAsync(new McpServerConfiguration
            {
                Id = "github",
                Transport = McpTransport.Stdio,
                Command = ["/bin/sh", "-c", $"printf '%s' \"$GITHUB_TOKEN\" > '{written}'; exec cat"],
                Env = Templates(("GITHUB_TOKEN", "${secret:gh-token}")),
                Allow = [new McpAllowEntry { Name = "*" }],
            });

            await WaitForFileAsync(written);
            Assert.Equal(TokenValue, await File.ReadAllTextAsync(written, Token));
        }
        finally
        {
            File.Delete(written);
        }
    }

    /// <summary>
    /// The SDK inherits by default, which would hand a third-party child process every other
    /// credential, token, and proxy setting this one holds.
    /// </summary>
    [Fact]
    public async Task AStdioServer_DoesNotInheritThisProcessesEnvironment_UnlessTheDocumentAsks()
    {
        const string ours = "AGENTCORE_MCP_LEAK_PROBE";
        var written = Path.Combine(Path.GetTempPath(), $"mcp-leak-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(ours, "a-secret-this-process-holds");

        try
        {
            await ConnectAndGiveUpAsync(new McpServerConfiguration
            {
                Id = "github",
                Transport = McpTransport.Stdio,
                Command = ["/bin/sh", "-c", $"printf '[%s]' \"${ours}\" > '{written}'; exec cat"],
                Allow = [new McpAllowEntry { Name = "*" }],
            });

            await WaitForFileAsync(written);
            Assert.Equal("[]", await File.ReadAllTextAsync(written, Token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ours, null);
            File.Delete(written);
        }
    }

    [Fact]
    public async Task AStdioServerThatAsksToInherit_GetsThisProcessesEnvironment()
    {
        const string ours = "AGENTCORE_MCP_INHERIT_PROBE";
        var written = Path.Combine(Path.GetTempPath(), $"mcp-inherit-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(ours, "visible");

        try
        {
            await ConnectAndGiveUpAsync(new McpServerConfiguration
            {
                Id = "github",
                Transport = McpTransport.Stdio,
                Command = ["/bin/sh", "-c", $"printf '[%s]' \"${ours}\" > '{written}'; exec cat"],
                InheritEnv = true,
                Allow = [new McpAllowEntry { Name = "*" }],
            });

            await WaitForFileAsync(written);
            Assert.Equal("[visible]", await File.ReadAllTextAsync(written, Token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ours, null);
            File.Delete(written);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // http: the header is on the request, and the request is on the host's own pipeline.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AnHttpServersHeaders_AreSentOnTheRequest_WithTheirSecretsResolved()
    {
        RecordingHandler handler = new();

        await ConnectAndGiveUpAsync(
            new McpServerConfiguration
            {
                Id = "jira",
                Transport = McpTransport.Http,
                Url = "https://mcp.example.com/",
                Headers = Templates(("Authorization", "Bearer ${secret:gh-token}")),
                Allow = [new McpAllowEntry { Name = "*" }],
            },
            handler);

        // The transport probes more than one shape of endpoint. Every request it makes has to carry
        // the credential: one that did not would be the request that gets refused.
        Assert.NotEmpty(handler.Seen);
        Assert.All(
            handler.Seen,
            request => Assert.Equal(
                $"Bearer {TokenValue}", Assert.Single(request.Headers.GetValues("Authorization"))));
    }

    /// <summary>
    /// Left to itself the transport builds its own <see cref="HttpClient"/>, which shares none of the
    /// host's proxy settings, certificate configuration, or logging.
    /// </summary>
    [Fact]
    public async Task AnHttpServer_IsReachedOnTheClientTheHostSupplies()
    {
        RecordingHandler handler = new();

        await ConnectAndGiveUpAsync(
            new McpServerConfiguration
            {
                Id = "jira",
                Transport = McpTransport.Http,
                Url = "https://mcp.example.com/",
                Allow = [new McpAllowEntry { Name = "*" }],
            },
            handler);

        Assert.NotEmpty(handler.Seen);
    }

    /// <summary>Runs one connection attempt that is never going to succeed, and swallows the failure.</summary>
    /// <remarks>
    /// Every test here asserts on what the attempt did on its way out — an environment the child was
    /// launched with, a header that went onto the wire — and none of them needs a server that
    /// answers. A short timeout keeps a doomed attempt from costing the suite anything.
    /// </remarks>
    private static async Task ConnectAndGiveUpAsync(
        McpServerConfiguration server, HttpMessageHandler? handler = null)
    {
        McpConnectionFactory factory = new(
            ResolvedSecrets.Create([new KeyValuePair<string, string>("gh-token", TokenValue)]),
            handler is null ? null : () => new HttpClient(handler, disposeHandler: false));

        try
        {
            await using var client = await factory.ConnectAsync(server, TimeSpan.FromSeconds(2), Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    private static Dictionary<string, SecretTemplate> Templates(params (string Key, string Value)[] entries)
        => entries.ToDictionary(entry => entry.Key, entry => SecretTemplate.Parse(entry.Value), StringComparer.Ordinal);

    /// <summary>Waits for the child process to have written its file.</summary>
    private static async Task WaitForFileAsync(string path)
    {
        for (var attempt = 0; attempt < 100 && !File.Exists(path); attempt++)
        {
            await Task.Delay(20, Token);
        }

        Assert.True(File.Exists(path), $"the child process never wrote {path}, so it was never launched.");
    }

    /// <summary>Records every request and refuses it, so no attempt reaches a real network.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _seen = [];

        private readonly Lock _sync = new();

        public IReadOnlyList<HttpRequestMessage> Seen
        {
            get
            {
                lock (_sync)
                {
                    return [.. _seen];
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _seen.Add(request);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}

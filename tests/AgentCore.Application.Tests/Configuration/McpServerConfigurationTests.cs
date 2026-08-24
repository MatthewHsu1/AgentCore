using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// What an <c>mcp:</c> entry may hold, and where a credential is allowed to go.
/// </summary>
/// <remarks>
/// An HTTP server behind authentication is reached with <c>headers:</c>, and a stdio server's
/// credential belongs in <c>env:</c>. Neither may go in <c>command:</c>, where every user on the
/// machine can read it out of <c>ps</c>.
/// </remarks>
public sealed class McpServerConfigurationTests
{
    private const string TokenName = "gh-token";
    private const string TokenValue = "ghp_0123456789";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------
    // A credential has somewhere to go, on either transport.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void AStdioServer_BindsItsEnvironment()
    {
        var document = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            name: stdio-env
            mcp:
              - id: github
                transport: stdio
                command: ["npx", "-y", "server-github"]
                env:
                  GITHUB_TOKEN: "${secret:gh-token}"
                  NODE_ENV: production
                allow: ["*"]
            """);

        var server = Assert.Single(document.Mcp);
        Assert.Equal("${secret:gh-token}", server.Env["GITHUB_TOKEN"].Raw);
        Assert.True(server.Env["GITHUB_TOKEN"].HasSecretReferences);

        // A plain value is config, not a credential, and is carried through untouched.
        Assert.Equal("production", server.Env["NODE_ENV"].Raw);
        Assert.False(server.Env["NODE_ENV"].HasSecretReferences);
    }

    [Fact]
    public void AnHttpServer_BindsItsHeaders()
    {
        var document = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            name: http-headers
            mcp:
              - id: jira
                transport: http
                url: https://mcp.example.com/
                headers:
                  Authorization: "Bearer ${secret:jira-token}"
                allow: ["*"]
            """);

        var server = Assert.Single(document.Mcp);
        Assert.Equal("Bearer ${secret:jira-token}", server.Headers["Authorization"].Raw);
    }

    [Fact]
    public async Task AnMcpCredential_IsResolvedAtStartup_LikeAToolHeader()
    {
        var document = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            name: mcp-secrets
            mcp:
              - id: github
                transport: stdio
                command: ["npx", "-y", "server-github"]
                env: { GITHUB_TOKEN: "${secret:gh-token}" }
                allow: ["*"]
              - id: jira
                transport: http
                url: https://mcp.example.com/
                headers: { Authorization: "Bearer ${secret:jira-token}" }
                allow: ["*"]
            """);

        var secrets = await ResolvedSecrets.ResolveAsync(
            document,
            new MapSecretResolver().With(TokenName, TokenValue).With("jira-token", "jira-abc"),
            Token);

        Assert.Equal(TokenValue, secrets.Format(document.Mcp[0].Env["GITHUB_TOKEN"]));
        Assert.Equal("Bearer jira-abc", secrets.Format(document.Mcp[1].Headers["Authorization"]));
    }

    [Fact]
    public async Task AnMcpCredentialThatResolvesToNothing_FailsTheBoot_NamingWhereItWasWritten()
    {
        var document = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            name: missing-secret
            mcp:
              - id: github
                transport: stdio
                command: ["npx", "-y", "server-github"]
                env: { GITHUB_TOKEN: "${secret:gh-token}" }
                allow: ["*"]
            """);

        var failure = await Assert.ThrowsAsync<SecretResolutionException>(
            async () => await ResolvedSecrets.ResolveAsync(document, new MapSecretResolver(), Token));

        Assert.Contains(TokenName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("/mcp/0/env/GITHUB_TOKEN", failure.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // A credential in command: or url: would leak while not even working, so the document is refused.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void ASecretWrittenIntoCommand_FailsTheDocument_AndPointsAtEnv()
    {
        var document = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            name: secret-in-argv
            mcp:
              - id: github
                transport: stdio
                command: ["server-github", "--token", "${secret:gh-token}"]
                allow: ["*"]
            """);

        var error = Assert.Single(ConfigurationValidator.EvaluateStructure(document).Errors);

        Assert.Equal("/mcp/0/command/2", error.Pointer);
        Assert.Contains("ps", error.Message, StringComparison.Ordinal);
        Assert.Contains("env:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecretWrittenIntoUrl_FailsTheDocument_AndPointsAtHeaders()
    {
        var document = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            name: secret-in-url
            mcp:
              - id: jira
                transport: http
                url: "https://mcp.example.com/?key=${secret:jira-token}"
                allow: ["*"]
            """);

        var error = Assert.Single(ConfigurationValidator.EvaluateStructure(document).Errors);

        Assert.Equal("/mcp/0/url", error.Pointer);
        Assert.Contains("headers:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AServerThatWritesNoSecretAnywhereOdd_Passes()
    {
        var document = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            name: clean
            mcp:
              - id: github
                transport: stdio
                command: ["npx", "-y", "server-github"]
                env: { GITHUB_TOKEN: "${secret:gh-token}" }
                allow: ["*"]
            """);

        Assert.Empty(ConfigurationValidator.EvaluateStructure(document).Errors);
    }

    // ---------------------------------------------------------------------------------------------
    // The transport branches: a key that belongs to the other transport is refused by check 1, so a
    // deployer never gets a header silently ignored on a child process.
    // ---------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("stdio", "command: [\"a\"]", "headers: { Authorization: x }")]
    [InlineData("http", "url: https://x.example.com/", "env: { A: b }")]
    [InlineData("http", "url: https://x.example.com/", "inheritEnv: true")]
    public void AKeyOfTheOtherTransport_IsRefused(string transport, string address, string wrongKey)
    {
        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationLoader.LoadYaml($"""
            apiVersion: agentcore/v1
            name: wrong-key
            mcp:
              - id: server
                transport: {transport}
                {address}
                {wrongKey}
                allow: ["*"]
            """));

        Assert.Contains("/mcp/0", failure.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // The timing knobs bind. What they do to a connection is McpServerSessionTests.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void TimeoutsAndRetry_Bind()
    {
        var document = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            name: timings
            mcp:
              - id: jira
                transport: http
                url: https://mcp.example.com/
                connectTimeoutSeconds: 3
                callTimeoutSeconds: 12
                retry: { attempts: 5, backoffMs: 250 }
                allow: ["*"]
            """);

        var server = Assert.Single(document.Mcp);
        Assert.Equal(3, server.ConnectTimeoutSeconds);
        Assert.Equal(12, server.CallTimeoutSeconds);
        Assert.Equal(5, server.Retry!.Attempts);
        Assert.Equal(250, server.Retry.BackoffMs);
    }

    [Fact]
    public void AServerThatNamesNoTimings_LeavesThemUnset_SoTheSessionDefaultsApply()
    {
        var document = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            name: no-timings
            mcp:
              - id: jira
                transport: http
                url: https://mcp.example.com/
                allow: ["*"]
            """);

        var server = Assert.Single(document.Mcp);
        Assert.Null(server.ConnectTimeoutSeconds);
        Assert.Null(server.CallTimeoutSeconds);
        Assert.Null(server.Retry);
    }

    /// <summary>
    /// The SDK's own default is to inherit, which would hand a third-party child process every other
    /// credential this one holds. A document has to ask for that.
    /// </summary>
    [Fact]
    public void InheritingTheHostEnvironment_IsOffUnlessTheDocumentAsks()
    {
        var document = ConfigurationLoader.LoadYaml("""
            apiVersion: agentcore/v1
            name: inherit
            mcp:
              - id: a
                transport: stdio
                command: ["a"]
                allow: ["*"]
              - id: b
                transport: stdio
                command: ["b"]
                inheritEnv: true
                allow: ["*"]
            """);

        Assert.False(document.Mcp[0].InheritEnv);
        Assert.True(document.Mcp[1].InheritEnv);
    }
}

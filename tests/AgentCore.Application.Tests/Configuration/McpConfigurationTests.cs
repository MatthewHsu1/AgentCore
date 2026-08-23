using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Xunit;

namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// The <c>mcp:</c> block of decision 13: parse and shape only, no connection.
/// </summary>
/// <remarks>
/// Each test pins one rule check 1 of section 8.5 enforces over an <c>mcp:</c> server: the
/// <c>allow:</c> entry shapes decision 6 permits, and the transport/command/url pairing decision 10's
/// served ids depend on.
/// </remarks>
public sealed class McpConfigurationTests
{
    private static readonly string[] JiraCommand = ["npx", "-y", "@atlassian/mcp"];

    [Fact]
    public void AStdioServerBindsItsCommandAndTwoAllowEntries()
    {
        var configuration = Load("""
            mcp:
              - id: jira
                transport: stdio
                command: [npx, -y, "@atlassian/mcp"]
                allow:
                  - create_issue
                  - search_issues: { as: find_ticket }
            """);

        var server = Assert.Single(configuration.Mcp);
        Assert.Equal("jira", server.Id);
        Assert.Equal(McpTransport.Stdio, server.Transport);
        Assert.Equal(JiraCommand, server.Command);

        Assert.Collection(
            server.Allow,
            entry =>
            {
                Assert.Equal("create_issue", entry.Name);
                Assert.Null(entry.As);
            },
            entry =>
            {
                Assert.Equal("search_issues", entry.Name);
                Assert.Equal("find_ticket", entry.As);
            });
    }

    [Fact]
    public void AWildcardAllowBindsToOneUnaliasedEntry()
    {
        var configuration = Load("""
            mcp:
              - id: jira
                transport: stdio
                command: [npx, -y, "@atlassian/mcp"]
                allow: ["*"]
            """);

        var entry = Assert.Single(Assert.Single(configuration.Mcp).Allow);
        Assert.Equal("*", entry.Name);
        Assert.Null(entry.As);
    }

    [Fact]
    public void AnAllowEntryMapWithTwoKeys_FailsTheLoad()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => Load("""
                mcp:
                  - id: jira
                    transport: stdio
                    command: [npx]
                    allow:
                      - { a: { as: x }, b: { as: y } }
                """));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer.StartsWith("/mcp", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAllowEntryMapWithAKeyOtherThanAs_FailsTheLoad()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => Load("""
                mcp:
                  - id: jira
                    transport: stdio
                    command: [npx]
                    allow:
                      - search_issues: { rename: find_ticket }
                """));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer.StartsWith("/mcp", StringComparison.Ordinal));
    }

    [Fact]
    public void AStdioServerWithNoCommand_FailsTheLoad()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => Load("""
                mcp:
                  - id: jira
                    transport: stdio
                """));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer.StartsWith("/mcp", StringComparison.Ordinal));
    }

    [Fact]
    public void AnHttpServerWithNoUrl_FailsTheLoad()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => Load("""
                mcp:
                  - id: jira
                    transport: http
                """));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer.StartsWith("/mcp", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownKeyOnAServer_FailsTheLoad()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => Load("""
                mcp:
                  - id: jira
                    transport: stdio
                    command: [npx]
                    nickname: jira-prod
                """));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer.StartsWith("/mcp", StringComparison.Ordinal));
    }

    /// <summary>Loads one <c>mcp:</c> section under the smallest complete document header.</summary>
    /// <param name="mcp">The <c>mcp:</c> section, written at the document's own margin.</param>
    /// <returns>The loaded document.</returns>
    private static AgentCoreConfiguration Load(string mcp)
        => ConfigurationLoader.LoadYaml(
            "apiVersion: agentcore/v1\nname: mcp-schema\n" + mcp);
}

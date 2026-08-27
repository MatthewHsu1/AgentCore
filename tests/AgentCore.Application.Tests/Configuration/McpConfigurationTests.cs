using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Xunit;

namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// The <c>mcp:</c> block of decision 13: parse and shape only, no connection.
/// </summary>
/// <remarks>
/// Each test pins one rule check 1 of section 8.5 enforces over an <c>mcp:</c> server: the
/// <c>allow:</c> entry shapes decision 6 permits, and the transport/command/url pairing a server's
/// connection depends on. Served ids depend on <c>id</c> and <c>allow[].as</c> (decision 10), not on
/// the transport.
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

    [Fact]
    public void AStdioServerWithAUrl_FailsTheLoad()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => Load("""
                mcp:
                  - id: jira
                    transport: stdio
                    command: [npx]
                    url: https://example.test
                """));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer.StartsWith("/mcp", StringComparison.Ordinal));
    }

    [Fact]
    public void AnHttpServerWithACommand_FailsTheLoad()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => Load("""
                mcp:
                  - id: jira
                    transport: http
                    url: https://example.test
                    command: [npx]
                """));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer.StartsWith("/mcp", StringComparison.Ordinal));
    }

    /// <summary>
    /// A null <c>allow:</c> entry is check 1's job, and check 1 already refuses it (see
    /// <see cref="AnAllowEntryMapWithTwoKeys_FailsTheLoad"/> and its neighbours). This test bypasses
    /// check 1 and binds a hand-built tree directly, the route <see cref="McpAllowEntryConverter"/>'s
    /// own failure branches exist for: without <c>HandleNull</c>, <c>JsonSerializer</c> never calls
    /// <c>Read</c> for a null token and simply stores a null reference in the list.
    /// </summary>
    [Fact]
    public void ANullAllowEntry_FailsThroughTheBinder()
    {
        var document = JsonNode.Parse("""
            {
                "apiVersion": "agentcore/v1",
                "name": "mcp-schema",
                "mcp": [
                    { "id": "jira", "transport": "stdio", "command": ["npx"], "allow": [null] }
                ]
            }
            """)!;

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationBinder.Bind(document));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
        Assert.Contains(failure.Errors, error => error.Pointer.StartsWith("/mcp", StringComparison.Ordinal));
    }

    /// <summary>Decision 10: a served MCP tool id carries the one dot that <c>tools:</c> otherwise forbids.</summary>
    [Fact]
    public void AnAgentReferencingADottedMcpToolId_PassesTheLoad()
    {
        var configuration = Load("""
            agents:
              items:
                - id: front
                  tools: [jira.create_issue]
            """);

        var agent = Assert.Single(configuration.Agents!.Items);
        Assert.Equal("jira.create_issue", Assert.Single(agent.Tools));
    }

    /// <summary>A tool id carries at most one dot; a second dot is not a served MCP id and is refused.</summary>
    [Fact]
    public void AnAgentReferencingATwoDottedToolId_FailsTheLoad()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => Load("""
                agents:
                  items:
                    - id: front
                      tools: [jira.create.issue]
                """));

        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
    }

    /// <summary>Loads a document section under the smallest complete document header.</summary>
    /// <param name="section">The section, written at the document's own margin.</param>
    /// <returns>The loaded document.</returns>
    private static AgentCoreConfiguration Load(string section)
        => ConfigurationLoader.LoadYaml(
            "apiVersion: agentcore/v1\nname: mcp-schema\n" + section);
}

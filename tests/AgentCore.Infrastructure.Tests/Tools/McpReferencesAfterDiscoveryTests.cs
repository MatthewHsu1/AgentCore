using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Tests.Tools.Fakes;
using AgentCore.Infrastructure.Tools;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Tools;

/// <summary>
/// Decision 15's reference pass, run against ids a real MCP server discovered.
/// </summary>
/// <remarks>
/// <para>
/// Task 5 wires this into <c>AgentCoreServiceCollectionExtensions.AddAgentCoreAsync</c>: once
/// <c>ToolRegistryStartup.BuildAsync</c> returns, the composition root unions the registry's own ids
/// with every declared <c>kind: agent</c> tool id, then calls
/// <see cref="ConfigurationValidator.ValidateToolReferences"/> against that union. This test cannot
/// exercise that composition-root code end to end from <c>AgentCore.AspNetCore.Tests</c>, where the
/// task brief asked for it: <see cref="McpToolSource"/>'s fake-transport constructor is
/// <see langword="internal"/>, its assembly's <c>InternalsVisibleTo</c> names only this project, and
/// <c>AgentCore.AspNetCore</c> itself never references <c>AgentCore.Infrastructure</c> at all —
/// <c>McpToolSource</c> is wired in only by <c>AgentCore.Hosting</c>, whose own test project carries
/// no such grant either. Reaching it truly end to end would need either widening that
/// <c>InternalsVisibleTo</c> (an owner call, since it broadens what one assembly exposes to another)
/// or a real out-of-process MCP server fixture (heavier and less deterministic than the in-process
/// rig this suite already has). Absent either, this test pins the same recipe one layer down: the
/// real MCP protocol, over the in-process transport <see cref="InProcessMcpServer"/> already wires
/// for <c>McpToolSourceTests</c>, feeding <see cref="ToolRegistryBuilder.BuildAsync"/> and then
/// <see cref="ConfigurationValidator.ValidateToolReferences"/> — the exact two calls task 5 connects.
/// </para>
/// <para>
/// Before task 5, nothing let a document reference an MCP tool at all: the reference pass ran
/// immediately after loading, against declared ids only, and an MCP-discovered id is never a
/// declared one. <see cref="AnAgentReferencingAnMcpDerivedId_ResolvesAfterDiscovery"/> is the
/// scenario that was broken.
/// </para>
/// </remarks>
public sealed class McpReferencesAfterDiscoveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAgentReferencingAnMcpDerivedId_ResolvesAfterDiscovery()
    {
        await using InProcessMcpServer fake = new("create_issue");
        await using McpToolSource source = new(_ => fake.ClientTransport);

        var configuration = new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Mcp =
            [
                new McpServerConfiguration
                {
                    Id = "jira",
                    Transport = McpTransport.Stdio,
                    Command = ["jira-mcp"],
                    Allow = [new McpAllowEntry { Name = "create_issue" }],
                },
            ],
            Agents = new AgentsConfiguration
            {
                Items =
                [
                    new AgentConfiguration
                    {
                        Id = "only",
                        Instructions = "I file tickets",
                        Tools = ["jira.create_issue"],
                    },
                ],
            },
        };

        var registry = await ToolRegistryBuilder.BuildAsync(
            [source], new ToolSourceContext(configuration), Token);

        var servedToolIds = registry.Ids.ToHashSet(StringComparer.Ordinal);
        foreach (var tool in configuration.Tools)
        {
            if (tool.Kind == ToolKind.Agent)
            {
                servedToolIds.Add(tool.Id);
            }
        }

        // Does not throw: 'jira.create_issue' is an MCP-discovered id, never a declared one, and
        // the reference pass must still resolve it against what the registry actually serves.
        ConfigurationValidator.ValidateToolReferences(configuration, servedToolIds);

        Assert.True(registry.Contains("jira.create_issue"));
        Assert.Equal("jira.create_issue", registry.Resolve("jira.create_issue").Name);
    }

    [Fact]
    public async Task AnAgentReferencingAnIdMcpDoesNotOffer_StillFailsAfterDiscovery()
    {
        await using InProcessMcpServer fake = new("create_issue");
        await using McpToolSource source = new(_ => fake.ClientTransport);

        var configuration = new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Mcp =
            [
                new McpServerConfiguration
                {
                    Id = "jira",
                    Transport = McpTransport.Stdio,
                    Command = ["jira-mcp"],
                    Allow = [new McpAllowEntry { Name = "create_issue" }],
                },
            ],
            Agents = new AgentsConfiguration
            {
                Items =
                [
                    new AgentConfiguration
                    {
                        Id = "only",
                        Instructions = "I file tickets",
                        Tools = ["jira.no_such_thing"],
                    },
                ],
            },
        };

        var registry = await ToolRegistryBuilder.BuildAsync(
            [source], new ToolSourceContext(configuration), Token);
        var servedToolIds = registry.Ids.ToHashSet(StringComparer.Ordinal);

        var failure = Assert.Throws<ConfigurationLoadException>(
            () => ConfigurationValidator.ValidateToolReferences(configuration, servedToolIds));

        Assert.Contains("jira.no_such_thing", failure.Message, StringComparison.Ordinal);
    }
}

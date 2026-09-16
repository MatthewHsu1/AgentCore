using System.Text.Json.Nodes;
using Xunit;

namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// The agentic keys and the values agentcore-v1.schema.json allows for them. The schema hardcodes
/// every spelling, so this is what catches the day someone changes a record and forgets the
/// document it describes.
/// </summary>
public sealed class AgenticKeySchemaTests
{
    [Fact]
    public void TheSchema_DeclaresEveryAgenticKeyTheRecordCarries()
    {
        var agent = Schema()["$defs"]!["agent"]!["properties"]!;

        List<string> keys = ["todos", "mode", "memory", "files", "shell", "approval", "background", "loop"];
        foreach (string key in keys)
        {
            Assert.True(agent[key] is not null, $"agentcore-v1.schema.json does not declare 'agent.{key}'.");
        }
    }

    [Fact]
    public void TheDefaults_DeclareTheInheritableKeysTheRecordCarries()
    {
        var defaults = Schema()["$defs"]!["agents"]!["properties"]!["defaults"]!["properties"]!;

        List<string> keys = ["todos", "mode", "approval"];
        foreach (string key in keys)
        {
            Assert.True(defaults[key] is not null, $"agentcore-v1.schema.json does not declare 'defaults.{key}'.");
        }
    }

    [Fact]
    public void TheStoresTheSchemaAllows_AreWorkspace()
    {
        Assert.True(JsonNode.DeepEquals(Enum("agentStore"), Parse("[\"workspace\"]")));
    }

    [Fact]
    public void TheShellKindsTheSchemaAllows_AreDockerAndLocal()
    {
        Assert.True(JsonNode.DeepEquals(Enum("shellKind"), Parse("[\"docker\", \"local\"]")));
    }

    [Fact]
    public void TheLoopConditionsTheSchemaAllows_AreTodosAndBackground()
    {
        var until = Schema()["$defs"]!["loopUntil"]!["properties"]!;

        Assert.True(until["todos"] is not null);
        Assert.True(until["background"] is not null);
    }

    private static JsonNode Enum(string name)
        => Schema()["$defs"]![name]!["enum"]!;

    private static JsonNode Parse(string json)
        => JsonNode.Parse(json)!;

    private static JsonNode Schema()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "AgentCore.Application", "Configuration", "Schema", "agentcore-v1.schema.json");
        Assert.True(File.Exists(path), $"The shipped schema is missing at '{path}'.");

        return JsonNode.Parse(File.ReadAllText(path))!;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentCore.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
namespace AgentCore.Application.Tools.Builtin;

/// <summary>The name of every tool AgentCore ships. <c>uses:</c> names one of these.</summary>
public static class BuiltinToolNames
{
    /// <summary>Lets the model provider run a web search of its own.</summary>
    public const string WebSearch = "web.search";

    /// <summary>Hands one file from the call's workspace to the person, through the blob store.</summary>
    public const string FilePublish = "file.publish";
}

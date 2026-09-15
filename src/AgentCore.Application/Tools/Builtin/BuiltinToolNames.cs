namespace AgentCore.Application.Tools.Builtin;

/// <summary>The name of every tool AgentCore ships. <c>uses:</c> names one of these.</summary>
public static class BuiltinToolNames
{
    /// <summary>Lets the model provider run a web search of its own.</summary>
    public const string WebSearch = "web.search";

    /// <summary>Lets the model provider run code of its own in a hosted sandbox.</summary>
    public const string CodeExecute = "code.execute";
}

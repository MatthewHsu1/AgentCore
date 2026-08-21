namespace AgentCore.Application.Tools.Builtin;

/// <summary>The name of every tool AgentCore ships. <c>uses:</c> names one of these.</summary>
public static class BuiltinToolNames
{
    /// <summary>Ranks the knowledge-base passages that answer one query.</summary>
    public const string KnowledgeSearch = "knowledge.search";

    /// <summary>Reads one whole knowledge-base document.</summary>
    public const string KnowledgeRead = "knowledge.read";

    /// <summary>Names the knowledge-base documents a glob keeps.</summary>
    public const string KnowledgeList = "knowledge.list";

    /// <summary>Finds the knowledge-base lines one regular expression matches.</summary>
    public const string KnowledgeGrep = "knowledge.grep";

    /// <summary>Draws one thing for the caller to look at.</summary>
    public const string Draw = "ui.draw";
}

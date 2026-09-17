using AgentCore.Application.Configuration.Parsing;
namespace AgentCore.Application.Configuration.Validation;

/// <summary>JSON pointers into the bound document, shared by every check of section 8.5.</summary>
internal static class ValidationPointer
{
    public const string Wildcard = "/providers/knowledge/scope/wildcard";

    public const string WildcardValue = Wildcard + "/value";

    public const string WildcardFacets = Wildcard + "/facets";

    public const string FromState = "/providers/knowledge/scope/fromState";

    public const string Ambiguity = "/providers/knowledge/ambiguity";

    public const string Mapper = "/providers/knowledge/mapper";

    /// <summary>The <c>agent:</c> field an entry, tool, stage, or node points at.</summary>
    public const string AgentField = "agent";

    public static string State(string slot) => ConfigurationError.AppendPointer("/state", slot);

    public static string Guard(string name) => ConfigurationError.AppendPointer("/guards", name);

    public static string Agent(int index) => ConfigurationError.AppendPointer("/agents/items", index);

    public static string AgentTool(int agent, int slot)
        => ConfigurationError.AppendPointer(ConfigurationError.AppendPointer(Agent(agent), "tools"), slot);

    public static string Tool(int index) => ConfigurationError.AppendPointer("/tools", index);

    public static string Mcp(int index) => ConfigurationError.AppendPointer("/mcp", index);

    public static string Entry(string name) => ConfigurationError.AppendPointer("/entries", name);

    public static string Policy(string name) => ConfigurationError.AppendPointer(Entry(name), "policy");

    public static string Graph(string name) => ConfigurationError.AppendPointer(Entry(name), "graph");

    public static string GraphAgents(string name) => ConfigurationError.AppendPointer(Graph(name), "agents");

    public static string GraphNodes(string name) => ConfigurationError.AppendPointer(Graph(name), "nodes");

    public static string GraphEdges(string name) => ConfigurationError.AppendPointer(Graph(name), "edges");

    public static string Stage(string name, int index)
        => ConfigurationError.AppendPointer(ConfigurationError.AppendPointer(Policy(name), "stages"), index);

    public static string Transition(string name, int stage, int exit)
        => ConfigurationError.AppendPointer(ConfigurationError.AppendPointer(Stage(name, stage), "to"), exit);

    public static string Node(string name, int index) => ConfigurationError.AppendPointer(GraphNodes(name), index);

    public static string Edge(string name, int index) => ConfigurationError.AppendPointer(GraphEdges(name), index);
}

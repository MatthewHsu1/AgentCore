namespace AgentCore.Application.Configuration.Schema;

/// <summary>How AgentCore talks to a declared MCP server.</summary>
public enum McpTransport
{
    /// <summary>The server is a child process, spoken to over its standard streams. <c>command:</c> launches it.</summary>
    Stdio,

    /// <summary>The server is reached over HTTP. <c>url:</c> names it.</summary>
    Http,
}

/// <summary>
/// One tool a server is allowed to serve.
/// </summary>
/// <remarks>
/// Decision 6: <c>allow:</c> is pinned by default and <c>"*"</c> is the explicit opt-out, because
/// these are live telephone calls and every tool offered spends prompt tokens on every turn a model
/// sees it, whether the call ever uses it or not.
/// </remarks>
public sealed record McpAllowEntry
{
    /// <summary>Gets the tool name the server offers, or <c>"*"</c> to allow every tool it offers.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the id this tool is served under, or <see langword="null"/> to take the default.
    /// </summary>
    /// <remarks>
    /// Decision 10: the served id is <c>&lt;server&gt;.&lt;tool&gt;</c> unless this renames it. An
    /// agent's <c>tools:</c> list references the served id like any other tool id.
    /// </remarks>
    public string? As { get; init; }
}

/// <summary>
/// One declared MCP server.
/// </summary>
/// <remarks>
/// Decision 13: a server declaration lives here and not in <c>tools:</c>, because one entry becomes
/// many tools, whose names and schemas are only known once the server answers — a shape the
/// one-declaration-to-one-tool <c>tools:</c> list cannot express.
/// </remarks>
public sealed record McpServerConfiguration
{
    /// <summary>Gets the server id. Decision 10 builds every served tool id from it.</summary>
    public required string Id { get; init; }

    /// <summary>Gets how AgentCore connects to the server.</summary>
    public required McpTransport Transport { get; init; }

    /// <summary>
    /// Gets the executable and its arguments. Set when <see cref="Transport"/> is <see cref="McpTransport.Stdio"/>.
    /// </summary>
    public IReadOnlyList<string> Command { get; init; } = [];

    /// <summary>Gets the server URL. Set when <see cref="Transport"/> is <see cref="McpTransport.Http"/>.</summary>
    public string? Url { get; init; }

    /// <summary>Gets the tools this server is pinned to offer.</summary>
    public IReadOnlyList<McpAllowEntry> Allow { get; init; } = [];
}

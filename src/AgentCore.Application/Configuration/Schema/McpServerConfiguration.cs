using System.Collections.ObjectModel;
using AgentCore.Application.Configuration.Parsing;

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
public sealed record McpAllowEntry
{
    /// <summary>Gets the tool name the server offers, or <c>"*"</c> to allow every tool it offers.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the id this tool is served under, or <see langword="null"/> to take the default.
    /// </summary>
    public string? As { get; init; }
}

/// <summary>
/// One declared MCP server.
/// </summary>
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

    /// <summary>
    /// Gets the headers sent on every request. Set when <see cref="Transport"/> is
    /// <see cref="McpTransport.Http"/>. A value may hold <c>${secret:name}</c> references.
    /// </summary>
    public IReadOnlyDictionary<string, SecretTemplate> Headers { get; init; }
        = ReadOnlyDictionary<string, SecretTemplate>.Empty;

    /// <summary>
    /// Gets the environment the child process is launched with. Set when <see cref="Transport"/> is
    /// <see cref="McpTransport.Stdio"/>. A value may hold <c>${secret:name}</c> references.
    /// </summary>
    public IReadOnlyDictionary<string, SecretTemplate> Env { get; init; }
        = ReadOnlyDictionary<string, SecretTemplate>.Empty;

    /// <summary>
    /// Gets whether the child process also inherits this process's own environment.
    /// </summary>
    public bool InheritEnv { get; init; }

    /// <summary>
    /// Gets how long one connection attempt may take, in seconds, or <see langword="null"/> for the
    /// default. It is applied on every attempt.
    /// </summary>
    public int? ConnectTimeoutSeconds { get; init; }

    /// <summary>
    /// Gets how long one tool call may take, in seconds, or <see langword="null"/> for the default.
    /// </summary>
    public int? CallTimeoutSeconds { get; init; }

    /// <summary>Gets what happens when a connection attempt fails, or <see langword="null"/> for the default.</summary>
    public McpRetryConfiguration? Retry { get; init; }
}

/// <summary>
/// What happens when a connection attempt to one MCP server fails.
/// </summary>
public sealed record McpRetryConfiguration
{
    /// <summary>Gets the number of attempts, including the first, or <see langword="null"/> for the default.</summary>
    public int? Attempts { get; init; }

    /// <summary>Gets the wait before the second attempt, in milliseconds. Each later one waits twice the last.</summary>
    public int? BackoffMs { get; init; }
}

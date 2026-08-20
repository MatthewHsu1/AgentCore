using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;

namespace AgentCore.Application.Configuration.Schema;

/// <summary>The kind of one declared tool.</summary>
public enum ToolKind
{
    /// <summary>A tool AgentCore ships. <c>uses:</c> names it.</summary>
    Builtin,

    /// <summary>A tool that calls one HTTP endpoint. <c>request:</c> describes it.</summary>
    Http,

    /// <summary>A tool that calls a host delegate. <c>binds:</c> names it.</summary>
    Binding,

    /// <summary>
    /// A tool that runs another declared agent and returns its reply. <c>agent:</c> names it.
    /// </summary>
    /// <remarks>
    /// This is agent-as-tool, and it is not handoff. The outer agent sees one function, calls it,
    /// and keeps control. The inner agent runs its own tool-calling loop and returns text. Handoff
    /// transfers control instead, and <c>graph:</c> with <c>pattern: handoff</c> already carries it,
    /// so the two do not overlap. Check 8 of section 8.5 walks the edges this kind declares.
    /// </remarks>
    Agent,
}

/// <summary>
/// The single HTTP call that a <see cref="ToolKind.Http"/> tool makes.
/// </summary>
public sealed record HttpRequestConfiguration
{
    /// <summary>Gets the HTTP method, in upper case.</summary>
    public required string Method { get; init; }

    /// <summary>Gets the URL. It may hold <c>{argument}</c> placeholders that the tool arguments fill.</summary>
    public required string Url { get; init; }

    /// <summary>Gets the request headers. A value may hold <c>${secret:name}</c> references.</summary>
    public IReadOnlyDictionary<string, SecretTemplate> Headers { get; init; } = ReadOnlyDictionary<string, SecretTemplate>.Empty;
}

/// <summary>
/// One declared tool.
/// </summary>
public sealed record ToolConfiguration
{
    /// <summary>Gets the tool id. An agent lists this id in its <c>tools:</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the kind of the tool.</summary>
    public required ToolKind Kind { get; init; }

    /// <summary>Gets the description the model reads, or <see langword="null"/>.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the built-in the tool calls. It is set when the kind is <see cref="ToolKind.Builtin"/>.</summary>
    public string? Uses { get; init; }

    /// <summary>Gets the host delegate the tool calls. It is set when the kind is <see cref="ToolKind.Binding"/>.</summary>
    public string? Binds { get; init; }

    /// <summary>
    /// Gets the id of the agent this tool runs. It is set when the kind is <see cref="ToolKind.Agent"/>.
    /// </summary>
    /// <remarks>
    /// The value names one entry of <c>agents.items</c>. It is the one explicit delegation edge in
    /// the document, so check 8 reads this field and never a naming convention.
    /// </remarks>
    public string? Agent { get; init; }

    /// <summary>Gets the raw JSON Schema of the tool arguments, or <see langword="null"/> when the tool takes none.</summary>
    public JsonNode? Parameters { get; init; }

    /// <summary>Gets the HTTP call. It is set when the kind is <see cref="ToolKind.Http"/>.</summary>
    public HttpRequestConfiguration? Request { get; init; }
}

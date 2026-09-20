using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.State;
using AgentCore.Domain.Knowledge;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Runtime;

// Everything one turn hands its tools, as one explicit value. The loop builds one per turn;
// the invoking client files it beside every tool call's arguments, so a tool declares what
// it needs as a parameter instead of reading the flow. Nested runs see the same turn with the
// clarifications stripped and the outermost call id stamped, via `with` copies the client makes.
internal sealed record TurnInvocation
{
    /// <summary>The options key the loop files the turn under, for the invoking client to read.
    /// Namespaced: no vendor field will ever be called this.</summary>
    internal const string ArgumentsKey = "urn:agentcore:turn";

    /// <summary>Gets the id of the conversation the turn belongs to.</summary>
    public required string ConversationId { get; init; }

    /// <summary>Gets the zero-based index of the turn now running.</summary>
    public required int TurnIndex { get; init; }

    /// <summary>Gets the stage the machine holds. Empty when the entry declares no policy.</summary>
    public required string Stage { get; init; }

    /// <summary>Gets the folder this conversation owns on disk, or <see langword="null"/>.</summary>
    public string? Workspace { get; init; }

    /// <summary>Gets this conversation's shell executors, or <see langword="null"/>.</summary>
    public ConversationShells? Shells { get; init; }

    /// <summary>Gets what the turn may see of the knowledge base, or <see langword="null"/>.</summary>
    public KnowledgeScope? Knowledge { get; init; }

    /// <summary>Gets the conversation's ambiguity holder, or <see langword="null"/> inside a nested tool call.</summary>
    public Clarifications? Clarifications { get; init; }

    /// <summary>Gets the instructions for this one invocation, or <see langword="null"/> for none.</summary>
    public string? Instructions { get; init; }

    /// <summary>Gets the zone the person on the conversation is in, or <see langword="null"/> when the host named none.</summary>
    public TimeZoneInfo? TimeZone { get; init; }

    /// <summary>Gets whether this row's session carries the caller's own history.</summary>
    public bool CarriesHistory { get; init; }

    /// <summary>Gets the conversation's logger, or <see langword="null"/> to log nowhere.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>Gets the screen this conversation draws on, or <see langword="null"/> when it has none.</summary>
    public IRenderPort? Screen { get; init; }

    /// <summary>Gets what the turn cites into. Never <see langword="null"/> on a loop-built turn.</summary>
    public TurnSources? Sources { get; init; }

    /// <summary>Gets what the turn draws into, or <see langword="null"/> when it has no screen.</summary>
    public TurnRenders? Renders { get; init; }

    /// <summary>Gets what the turn's tools answer into. Never <see langword="null"/> on a loop-built turn.</summary>
    public TurnResults? Results { get; init; }

    /// <summary>Gets what the turn publishes files into. Never <see langword="null"/> on a loop-built turn.</summary>
    public TurnFiles? Files { get; init; }

    /// <summary>
    /// Lists every kind of thing this turn's tools produce for the caller, in the order they attach to
    /// a tool-result message. A new kind is one new property and one name here.
    /// </summary>
    internal IReadOnlyList<ITurnAttachments> Attachments()
        => [.. new ITurnAttachments?[] { Renders, Sources, Files }.OfType<ITurnAttachments>()];

    /// <summary>Gets the tools a delegated run of this conversation is offered, or <see langword="null"/> for none.</summary>
    public IReadOnlyList<AITool>? Tools { get; init; }

    /// <summary>Gets the id of the delegating tool <see cref="Tools"/> are meant for.</summary>
    public string? ToolsFor { get; init; }

    /// <summary>Gets what to do with a tool failure the run reports.</summary>
    public Action<ToolFailure>? OnToolFailure { get; init; }

    /// <summary>Gets the outermost tool call open on this flow, stamped by the invoking client,
    /// or <see langword="null"/> before the first tool call.</summary>
    public string? OuterCallId { get; init; }

    /// <summary>Gets whether this turn runs nested inside another tool call. A nested turn neither latches the holder nor records.</summary>
    public bool Nested { get; init; }

    /// <summary>
    /// Gets the live state document of the conversation running this turn, or <see langword="null"/>
    /// outside a turn. The graph-state wrapper snapshots it into the run just before the run starts;
    /// nothing writes the document mid-run (writers and the extractor commit after), so the snapshot
    /// reads what the old ambient read at edge time.
    /// </summary>
    public StateDocument? State { get; init; }

    /// <summary>Reads the turn filed on a run's options by <see cref="RunOptions"/>, or null when there is none.</summary>
    internal static TurnInvocation? From(AgentRunOptions? options)
        => options is ChatClientAgentRunOptions run ? From(run.ChatOptions) : null;

    /// <summary>Reads the turn filed on the options one request carries, or null outside a turn.</summary>
    internal static TurnInvocation? From(ChatOptions? options)
        => options?.AdditionalProperties?.TryGetValue(ArgumentsKey, out var filed) == true
            ? filed as TurnInvocation
            : null;

    /// <summary>Reads the turn filed beside one tool call's arguments by <see cref="FileIn"/>, or null outside a turn.</summary>
    internal static TurnInvocation? FiledIn(AIFunctionArguments? arguments)
        => arguments?.Context?.TryGetValue(typeof(TurnInvocation), out var filed) == true
            ? filed as TurnInvocation
            : null;

    /// <summary>
    /// Files this turn beside one tool call's arguments, where the runtime-bound parameters read it.
    /// Beside, never among: the arguments dictionary is the model's own call record, and a checkpoint
    /// persists it, while <see cref="AIFunctionArguments.Context"/> lives only as long as the call.
    /// </summary>
    /// <returns>The same arguments, for a caller that builds them inline.</returns>
    internal AIFunctionArguments FileIn(AIFunctionArguments arguments)
    {
        arguments.Context ??= new Dictionary<object, object?>();
        arguments.Context[typeof(TurnInvocation)] = this;
        return arguments;
    }

    /// <summary>Builds the per-run options carrying this turn to the invoking client.</summary>
    internal ChatClientAgentRunOptions RunOptions()
    {
        ChatOptions chat = new()
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [ArgumentsKey] = this },
        };

        return new ChatClientAgentRunOptions(chat);
    }
}

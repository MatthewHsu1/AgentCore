using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.State;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime;

// Everything one turn hands its tools, as one explicit value. The loop builds one per turn;
// the invoking client snapshots it into every tool call's arguments, so a tool declares what
// it needs as a parameter instead of reading the flow. Nested runs see the same turn with the
// clarifications stripped and the outermost call id stamped, via `with` copies the client makes.
internal sealed record TurnInvocation
{
    /// <summary>The argument name the invoking client files the turn under. Namespaced: a model
    /// argument will never be called this, and the binder intercepts the type before JSON binding.</summary>
    internal const string ArgumentsKey = "urn:agentcore:turn";

    /// <summary>Gets the id of the call the turn belongs to.</summary>
    public required string CallId { get; init; }

    /// <summary>Gets the zero-based index of the turn now running.</summary>
    public required int TurnIndex { get; init; }

    /// <summary>Gets the stage the machine holds. Empty when the entry declares no policy.</summary>
    public required string Stage { get; init; }

    /// <summary>Gets the folder this call owns on disk, or <see langword="null"/>.</summary>
    public string? Workspace { get; init; }

    /// <summary>Gets this call's shell executors, or <see langword="null"/>.</summary>
    public CallShells? Shells { get; init; }

    /// <summary>Gets what the turn may see of the knowledge base, or <see langword="null"/>.</summary>
    public KnowledgeScope? Knowledge { get; init; }

    /// <summary>Gets the call's ambiguity holder, or <see langword="null"/> inside a nested tool call.</summary>
    public Clarifications? Clarifications { get; init; }

    /// <summary>Gets the instructions for this one invocation, or <see langword="null"/> for none.</summary>
    public string? Instructions { get; init; }

    /// <summary>Gets whether this row's session carries the caller's own history.</summary>
    public bool CarriesHistory { get; init; }

    /// <summary>Gets the screen this call draws on, or <see langword="null"/> when it has none.</summary>
    public IRenderPort? Screen { get; init; }

    /// <summary>Gets what the turn cites into. Never <see langword="null"/> on a loop-built turn.</summary>
    public TurnSources? Sources { get; init; }

    /// <summary>Gets what the turn draws into, or <see langword="null"/> when it has no screen.</summary>
    public TurnRenders? Renders { get; init; }

    /// <summary>Gets what the turn's tools answer into. Never <see langword="null"/> on a loop-built turn.</summary>
    public TurnResults? Results { get; init; }

    /// <summary>Gets the tools a delegated run of this call is offered, or <see langword="null"/> for none.</summary>
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
    /// Gets the live state document of the call running this turn, or <see langword="null"/>
    /// outside a turn. The graph-state wrapper snapshots it into the run just before the run starts;
    /// nothing writes the document mid-run (writers and the extractor commit after), so the snapshot
    /// reads what the old ambient read at edge time.
    /// </summary>
    public StateDocument? State { get; init; }

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

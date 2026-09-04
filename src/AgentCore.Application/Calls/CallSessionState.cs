using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace AgentCore.Application.Calls;

/// <summary>Everything one call's session holds that has no other durable home.</summary>
public sealed record CallSessionState
{
    /// <summary>The slots of a state whose writers filled none.</summary>
    private static readonly IReadOnlyDictionary<string, JsonNode?> NoSlots =
        ReadOnlyDictionary<string, JsonNode?>.Empty;

    /// <summary>The slots of a call whose ambiguity channels asked about none.</summary>
    private static readonly IReadOnlyDictionary<string, CallClarificationState> NoClarifications =
        ReadOnlyDictionary<string, CallClarificationState>.Empty;

    /// <summary>The shape this version of the library writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Gets the shape this blob was written in.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Gets the next free ordinal of the call.</summary>
    public int NextOrdinal { get; init; }

    /// <summary>Gets the index the call's next turn takes.</summary>
    public int NextTurnIndex { get; init; }

    /// <summary>Gets the stage the machine held. It is empty when the document declares no policy.</summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>Gets whether the machine had already reached a terminal stage.</summary>
    public bool IsComplete { get; init; }

    /// <summary>Gets the declared slots a writer had filled, by name. An unfilled slot is absent.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Slots { get; init; } = NoSlots;

    /// <summary>
    /// Gets what each slot's ambiguity channels have spent of their ask budget, by slot name. A slot
    /// nothing has asked about is absent.
    /// </summary>
    public IReadOnlyDictionary<string, CallClarificationState> Clarifications { get; init; } = NoClarifications;
}

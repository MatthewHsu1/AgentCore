namespace AgentCore.Application.Configuration.Parsing;

/// <summary>
/// The load-time check that rejected a document. The numbers are the rows of the table in section 8.5,
/// except <see cref="ValueRange"/>, which that table has no row for.
/// </summary>
public enum ConfigurationCheck
{
    /// <summary>The document is not well-formed YAML or JSON. This runs before check 1.</summary>
    Syntax = 0,

    /// <summary>Check 1: JSON Schema over the document. It fails on any shape error.</summary>
    DocumentSchema = 1,

    /// <summary>Check 2: reference resolution. It fails on an unknown agent, tool, guard, stage, or state slot.</summary>
    ReferenceResolution = 2,

    /// <summary>Check 3: one writer for each slot. It fails on zero writers, or two.</summary>
    SlotWriters = 3,

    /// <summary>Check 4: guard operators and variables.</summary>
    GuardOperators = 4,

    /// <summary>Check 5: exclusivity and coverage by evaluation.</summary>
    GuardExclusivity = 5,

    /// <summary>Check 6: reachability of every stage and node.</summary>
    Reachability = 6,

    /// <summary>Check 7: graph well-formedness.</summary>
    GraphWellFormedness = 7,

    /// <summary>Check 8: delegation cycles through an agent-as-tool loop.</summary>
    DelegationCycles = 8,

    /// <summary>
    /// Check 9: a configured count or interval outside the range the runtime accepts. It exists so a
    /// value that a timer or a cancellation source would throw on is refused at load, where the error
    /// still carries a pointer into the document.
    /// </summary>
    ValueRange = 9,
}

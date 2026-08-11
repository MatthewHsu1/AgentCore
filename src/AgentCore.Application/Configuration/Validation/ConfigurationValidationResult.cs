using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>
/// What checks 2 to 8 of section 8.5 found in one bound document.
/// </summary>
/// <remarks>
/// Section 8.7 fails the load fast, so <see cref="Errors"/> stops the document. A warning does not:
/// check 5 raises one when the state domain passes 65,536 points and it samples instead of walking
/// the whole product, which makes its coverage partial rather than complete.
/// </remarks>
public sealed record ConfigurationValidationResult
{
    /// <summary>Gets the result with nothing to report.</summary>
    public static ConfigurationValidationResult Clean { get; } = new()
    {
        Errors = EquatableList<ConfigurationError>.Empty,
        Warnings = EquatableList<ConfigurationError>.Empty,
    };

    /// <summary>Gets every reason the document fails, in check order.</summary>
    public required EquatableList<ConfigurationError> Errors { get; init; }

    /// <summary>Gets every partial-coverage warning, in check order. A warning does not fail the load.</summary>
    public required EquatableList<ConfigurationError> Warnings { get; init; }

    /// <summary>Reports whether the document passes checks 2 to 8.</summary>
    public bool IsValid => Errors.Count == 0;
}

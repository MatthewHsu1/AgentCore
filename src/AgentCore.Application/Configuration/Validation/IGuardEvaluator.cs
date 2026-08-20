using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>
/// Evaluates a JSONLogic guard against a snapshot of the declared state.
/// </summary>
/// <remarks>
/// <para>
/// Section 8.4 of the design defines the operator allow-list that an implementation accepts. Check 4
/// of section 8.5 rejects a rule that leaves that list, so a rule reaching this interface at run time
/// has already passed validation.
/// </para>
/// <para>
/// Section 8.7 fixes the run-time failure behaviour: a guard that throws is possible and is not a
/// defect. An implementation logs the failure once and returns <see langword="false"/>. It never
/// propagates the exception, because a failed guard must not drop a call.
/// </para>
/// <para>
/// An implementation resolves a named guard through the <c>guards:</c> table that it receives when it
/// is constructed. A name that the table does not hold is a check 2 failure at load time, so at run
/// time an implementation treats it as <see langword="false"/>.
/// </para>
/// </remarks>
public interface IGuardEvaluator
{
    /// <summary>
    /// Evaluates a named or inline guard reference against a state snapshot.
    /// </summary>
    /// <param name="guard">The guard reference taken from a stage transition or a graph edge.</param>
    /// <param name="state">
    /// The declared state slots and the three reserved slots in
    /// <see cref="ReservedStateSlots"/>, keyed by slot name. A <see langword="null"/> value means the
    /// slot is unfilled.
    /// </param>
    /// <returns><see langword="true"/> when the guard holds; otherwise <see langword="false"/>.</returns>
    bool Evaluate(GuardReference guard, IReadOnlyDictionary<string, JsonNode?> state);

    /// <summary>
    /// Evaluates a raw JSONLogic rule against a state snapshot.
    /// </summary>
    /// <param name="rule">The JSONLogic rule.</param>
    /// <param name="state">
    /// The declared state slots and the three reserved slots in
    /// <see cref="ReservedStateSlots"/>, keyed by slot name. A <see langword="null"/> value means the
    /// slot is unfilled.
    /// </param>
    /// <returns><see langword="true"/> when the rule holds; otherwise <see langword="false"/>.</returns>
    bool Evaluate(JsonNode rule, IReadOnlyDictionary<string, JsonNode?> state);
}

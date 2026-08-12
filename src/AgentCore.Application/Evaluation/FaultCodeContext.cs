using Microsoft.Extensions.AI.Evaluation;

namespace AgentCore.Application.Evaluation;

/// <summary>
/// The fault codes access path 1 resolved for one turn.
/// </summary>
/// <remarks>
/// <para>
/// Decision D10 makes fault-code lookup a path convention, so the turn already knows which codes it
/// read: <c>(model, code)</c> named a file and the agent read that file. This context carries those
/// codes to <see cref="FaultCodeEvaluator"/>, which is the only reader of it.
/// </para>
/// <para>
/// The context holds what the lookup resolved, not what the reply says. Section 9 makes the point
/// this exists for: the lookup is deterministic and the data is not, so the reply is the part nothing
/// else checks.
/// </para>
/// </remarks>
public sealed class FaultCodeContext : EvaluationContext
{
    /// <summary>The name this context carries.</summary>
    public const string FaultCodeContextName = "Fault Codes";

    /// <summary>Builds a context from the codes the lookup resolved.</summary>
    /// <param name="codes">
    /// The fault codes access path 1 resolved, written as the knowledge base writes them.
    /// </param>
    /// <exception cref="ArgumentNullException">The codes are <see langword="null"/>.</exception>
    public FaultCodeContext(IEnumerable<string> codes)
        : this(Materialize(codes))
    {
    }

    private FaultCodeContext(string[] codes)
        : base(FaultCodeContextName, string.Join(", ", codes))
        => Codes = codes;

    /// <summary>Gets the fault codes access path 1 resolved.</summary>
    public IReadOnlyList<string> Codes { get; }

    private static string[] Materialize(IEnumerable<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        return [.. codes];
    }
}

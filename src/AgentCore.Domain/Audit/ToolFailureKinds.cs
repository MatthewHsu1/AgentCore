namespace AgentCore.Domain.Audit;

/// <summary>
/// The wire token of each <see cref="ToolFailureKind"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>tool.failed</c> payload holds this token under <see cref="AuditPayloadKeys.ToolFailureKind"/>,
/// and never the .NET member name, and never the numeric value of the enum. This is the treatment
/// <see cref="CallEndReasons"/> already gives an end reason, and it is the same argument: a rename of a
/// C# member must not change a hash PostgreSQL already stored, and the <c>CHECK</c> constraint of D23
/// recomputes the same SHA-256 inside the engine, where no enum exists.
/// </para>
/// <para>
/// A token is stable forever. Add a token beside the old one, and never edit one in place.
/// </para>
/// <para>
/// A token reads <c>tool.&lt;what happened to it&gt;</c>, which is the
/// <c>&lt;subject&gt;.&lt;what happened&gt;</c> shape <see cref="CallEndReasons"/> uses. The tokens are
/// deliberately NOT the framework's <c>NotFound</c> and <c>Exception</c>: those name a C# enum whose
/// stability Microsoft does not promise, and a stored row must not depend on it.
/// </para>
/// </remarks>
public static class ToolFailureKinds
{
    /// <summary>Reads the wire token of one kind.</summary>
    /// <param name="kind">The kind to name.</param>
    /// <returns>The token the <c>tool.failed</c> payload writes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a member of the closed set.</exception>
    public static string ToToken(ToolFailureKind kind) => kind switch
    {
        ToolFailureKind.Undeclared => "tool.undeclared",
        ToolFailureKind.Faulted => "tool.faulted",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The tool-failure vocabulary is closed, and this value is not in it."),
    };

    /// <summary>Reads the kind behind one wire token.</summary>
    /// <param name="token">The token a stored row holds.</param>
    /// <param name="kind">The kind, when the token is known.</param>
    /// <returns><see langword="true"/> when the token names a kind.</returns>
    public static bool TryParse(string? token, out ToolFailureKind kind)
    {
        switch (token)
        {
            case "tool.undeclared": kind = ToolFailureKind.Undeclared; return true;
            case "tool.faulted": kind = ToolFailureKind.Faulted; return true;
            default: kind = default; return false;
        }
    }
}

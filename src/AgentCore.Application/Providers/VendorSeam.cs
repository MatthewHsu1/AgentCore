namespace AgentCore.Application.Providers;

/// <summary>
/// What one vendor seam calls itself, so a shared selector can write its failures.
/// </summary>
/// <param name="DocumentPath">
/// The dotted path a reader would find in the document, such as <c>providers.speech</c>.
/// </param>
/// <param name="Pointer">
/// The JSON pointer the failure carries, such as <c>/providers/telemetry/kind</c>. This is what a
/// tool uses to underline the offending line, so it must address the <c>kind</c> itself.
/// </param>
/// <param name="RegistrationHint">
/// The call a host makes to register a vendor for this seam, such as <c>options.UseSpeech(...)</c>.
/// A host that registered nothing has no kind to be offered instead, so the failure names the call
/// to make rather than an empty list.
/// </param>
/// <param name="Plural">
/// The plural noun this seam calls two of its vendors, such as <c>stores</c>. It exists so one
/// selector can preserve four seams' wording: knowledge writes <c>stores</c>, telemetry writes
/// <c>collectors</c>, moderation writes <c>endpoints</c>, and speech writes <c>vendors</c>. A seam
/// that names none through the three-argument constructor gets <c>vendors</c>.
/// </param>
public readonly record struct VendorSeam(
    string DocumentPath,
    string Pointer,
    string RegistrationHint,
    string Plural)
{
    /// <summary>Names a seam whose plural noun is the default <c>vendors</c>.</summary>
    /// <param name="documentPath">The dotted path a reader would find in the document.</param>
    /// <param name="pointer">The JSON pointer the failure carries.</param>
    /// <param name="registrationHint">The call a host makes to register a vendor for this seam.</param>
    public VendorSeam(string documentPath, string pointer, string registrationHint)
        : this(documentPath, pointer, registrationHint, "vendors")
    {
    }
}

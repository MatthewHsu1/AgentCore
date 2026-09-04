using System.Text;
using AgentCore.Application.State;
using Xunit;

namespace AgentCore.Application.Unicode.Tests.State;

/// <summary>
/// The <see cref="VocabularyFold"/> claims that hold only where the runtime composes Unicode. They
/// live in this project rather than beside the rest of the fold's rows because it is the one that
/// sets <c>InvariantGlobalization=false</c>; under the repo-wide <c>true</c> that production ships,
/// NFC is a no-op and both rows below would fail.
/// </summary>
/// <remarks>
/// Every non-ASCII literal here is a \uXXXX escape sequence, never a typed character. Round 12 of
/// the design's review wrote a decomposed probe input as typed characters, which an editor or a
/// file-writing tool had already re-composed on disk, so the check silently measured nothing.
/// </remarks>
public sealed class VocabularyFoldCompositionTests
{
    [Fact]
    public void Fold_DecomposedAndComposedSpellingsOfOneValue_Collide()
    {
        // U+0041 U+030A is "A" followed by a combining ring above (NFD); U+00C5 is the precomposed
        // Angstrom sign (NFC). NFC composition is what makes the two fold alike.
        var decomposed = VocabularyFold.Fold("\u0041\u030A900");
        var composed = VocabularyFold.Fold("\u00C5900");

        Assert.Equal(composed, decomposed);
    }

    [Fact]
    public void Probe_DecomposedARing_ComposesUnderThisProjectsIcu()
    {
        // The exact K44 boot probe (design doc section 10). Its counterpart in
        // AgentCore.AspNetCore.Tests asserts the opposite under that project's invariant runtime,
        // so the pair pins both sides of the condition K44 branches on.
        Assert.Equal("\u00C5", "\u0041\u030A".Normalize(NormalizationForm.FormC));
    }
}

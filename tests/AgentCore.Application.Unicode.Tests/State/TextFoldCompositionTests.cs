using AgentCore.Application.State;
using Xunit;

namespace AgentCore.Application.Unicode.Tests.State;

/// <summary>
/// The <see cref="TextFold"/> claim that holds only where the runtime composes Unicode. It lives
/// in this project rather than beside the rest of the fold's rows because it is the one that sets
/// <c>InvariantGlobalization=false</c>; under the repo-wide <c>true</c> that production ships, NFC
/// is a no-op and the row below would fail.
/// </summary>
/// <remarks>
/// Every non-ASCII literal here is a \uXXXX escape sequence, never a typed character. A decomposed
/// input written as typed characters is re-composed on disk by an editor or a file-writing tool,
/// so the check would silently measure nothing.
/// </remarks>
public sealed class TextFoldCompositionTests
{
    [Fact]
    public void Fold_DecomposedAndComposedSpellingsOfOneValue_Collide()
    {
        // U+0041 U+030A is "A" followed by a combining ring above (NFD); U+00C5 is the precomposed
        // Angstrom sign (NFC). NFC composition is what makes the two fold alike.
        var decomposed = TextFold.Fold("\u0041\u030A900");
        var composed = TextFold.Fold("\u00C5900");

        Assert.Equal(composed, decomposed);
    }
}

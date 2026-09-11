using AgentCore.Application.Runtime;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// The probe's note (§8), rendered against §4's own <c>description:</c> values so a human can read
/// the result as English rather than a template.
/// </summary>
public sealed class ClarificationTextTests
{
    // §4's own value, verbatim: "The model, as printed on the machine."
    private const string AppliesToDescription = "The model, as printed on the machine.";

    // §4's own value, verbatim: "The brand of the caller's machine."
    private const string BrandDescription = "The brand of the caller's machine.";

    [Fact]
    public void Note_TwoCandidates_JoinsWithAPlainComma_NotOr()
    {
        var text = ClarificationText.Note(AppliesToDescription, ["ct900", "ct900ent"], maxCandidates: 6);

        Assert.Equal(
            "One thing decides the answer here and is not yet known: The model, as printed on the machine. "
            + "It could be: ct900, ct900ent. Ask the caller which, and do not answer from the knowledge base "
            + "about it until they say.",
            text);
    }

    [Fact]
    public void Note_OneCandidate_RendersTheConfirmSentence()
    {
        var text = ClarificationText.Note(AppliesToDescription, ["ct900"], maxCandidates: 6);

        Assert.Equal(
            "One thing decides the answer here and is not yet confirmed: The model, as printed on the "
            + "machine. Everything found is for ct900. Ask the caller whether that is what they have before "
            + "answering from the knowledge base about it.",
            text);
    }

    [Fact]
    public void Note_OverMaxCandidates_OmitsTheList()
    {
        var text = ClarificationText.Note(
            AppliesToDescription,
            ["ct800", "ct800ent", "ct900", "ct900ent", "xt285", "xt385", "xt485"],
            maxCandidates: 6);

        Assert.Equal(
            "One thing decides the answer here and is not yet known: The model, as printed on the machine. "
            + "Ask the caller, and do not answer from the knowledge base about it until they say.",
            text);
    }
}

using AgentCore.Application.Runtime;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// The sentences §7 specifies for both ambiguity channels, rendered against §4's own
/// <c>description:</c> values so a human can read the result as English rather than a template.
/// </summary>
public sealed class ClarificationTextTests
{
    // §4's own value, verbatim: "The model, as printed on the machine."
    private const string AppliesToDescription = "The model, as printed on the machine.";

    // §4's own value, verbatim: "The brand of the caller's machine."
    private const string BrandDescription = "The brand of the caller's machine.";

    [Fact]
    public void Instruction_TwoCandidates_FirstMessage_RendersTheBaseSentence()
    {
        var text = ClarificationText.Instruction(
            AppliesToDescription, ["ct900", "ct900ent"], maxCandidates: 6, first: true);

        Assert.Equal(
            "One thing is not yet known: The model, as printed on the machine. It is one of ct900 or "
            + "ct900ent. Ask the caller, and do not give advice that is specific to one until they answer. "
            + "Anything that applies to all of them is still fair game.",
            text);
    }

    [Fact]
    public void Instruction_ThreeCandidates_JoinsWithAnOxfordComma()
    {
        var text = ClarificationText.Instruction(
            AppliesToDescription, ["ct800", "ct900", "ct900ent"], maxCandidates: 6, first: true);

        Assert.Equal(
            "One thing is not yet known: The model, as printed on the machine. It is one of ct800, ct900, "
            + "or ct900ent. Ask the caller, and do not give advice that is specific to one until they answer. "
            + "Anything that applies to all of them is still fair game.",
            text);
    }

    [Fact]
    public void Instruction_OneCandidate_RendersTheConfirmSentence()
    {
        var text = ClarificationText.Instruction(
            AppliesToDescription, ["ct900"], maxCandidates: 6, first: true);

        Assert.Equal(
            "One thing is not yet confirmed: The model, as printed on the machine. Everything found is for "
            + "ct900. Ask the caller whether that is what they have before giving advice specific to it.",
            text);
    }

    [Fact]
    public void Instruction_OverMaxCandidates_OmitsTheList()
    {
        var text = ClarificationText.Instruction(
            AppliesToDescription,
            ["ct800", "ct800ent", "ct900", "ct900ent", "xt285", "xt385", "xt485"],
            maxCandidates: 6,
            first: true);

        Assert.Equal(
            "One thing is not yet known: The model, as printed on the machine. Ask the caller, and do not "
            + "give advice specific to one until they answer.",
            text);
    }

    [Fact]
    public void Instruction_SecondMessage_OpensWithAnotherThing()
    {
        var text = ClarificationText.Instruction(
            BrandDescription, ["sole", "spirit"], maxCandidates: 6, first: false);

        Assert.StartsWith("Another thing is not yet known: ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("One thing is not yet known", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Instruction_SecondMessage_OneCandidate_OpensWithAnotherThing()
    {
        var text = ClarificationText.Instruction(
            AppliesToDescription, ["ct900"], maxCandidates: 6, first: false);

        Assert.StartsWith("Another thing is not yet confirmed: ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Instruction_DescriptionFallsBackToTheSlotName_WhenTheCallerPassesIt()
    {
        // ClarificationText itself does not know about slots; the fallback to the slot name is the
        // caller's job (ClarificationProvider). This fact only pins that the renderer treats
        // whatever string it is given as the apposition, verbatim.
        var text = ClarificationText.Instruction("applies_to", ["ct900", "ct900ent"], maxCandidates: 6, first: true);

        Assert.Equal(
            "One thing is not yet known: applies_to It is one of ct900 or ct900ent. Ask the caller, and do "
            + "not give advice that is specific to one until they answer. Anything that applies to all of "
            + "them is still fair game.",
            text);
    }

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

    [Fact]
    public void TwoOverCapSets_RenderTheIdenticalSentence()
    {
        // K37: two different over-maxCandidates sets say the same thing, which is what lets the
        // sentinel record suppress the second one as a repeat rather than a new question.
        var first = ClarificationText.Instruction(
            AppliesToDescription, ["a", "b", "c", "d", "e", "f", "g"], maxCandidates: 6, first: true);
        var second = ClarificationText.Instruction(
            AppliesToDescription, ["h", "i", "j", "k", "l", "m", "n"], maxCandidates: 6, first: true);

        Assert.Equal(first, second);
    }
}

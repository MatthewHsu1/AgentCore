using AgentCore.Application.Runtime;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// <see cref="SpokenAsk"/> and the <see cref="Clarifications.CommitAsks"/> gate it drives: the ask
/// is charged to the slot only where the turn's own reply actually put the question.
/// </summary>
public sealed class SpokenAskTests
{
    [Fact]
    public void NamedIn_ReplyNamesOneOfTheCandidates_IsTrue()
        => Assert.True(SpokenAsk.NamedIn("Is it the ct900 or the ct900ent?", Named("ct900", "ct900ent")));

    [Fact]
    public void NamedIn_ReplyNamesNone_IsFalse()
        => Assert.False(SpokenAsk.NamedIn("I'm sorry to hear that. What is going wrong?", Named("ct900", "ct900ent")));

    [Fact]
    public void NamedIn_ReplyRespellsACandidate_IsTrue()
    {
        // The fold drops separators, so a model that writes the value back with a space where the
        // collection has a hyphen still counts as having asked.
        Assert.True(SpokenAsk.NamedIn("Is it the F63 2013?", Named("F63-2013", "F63-2023")));
    }

    [Fact]
    public void NamedIn_AnOverCapRecord_IsTrue()
    {
        // The over-cap instruction lists nothing, so there is no name to look for. Charging it is
        // the only outcome that does not drop the ask on every turn alike.
        Assert.True(SpokenAsk.NamedIn("I'm sorry to hear that.", Clarifications.LastNamed.TooMany));
    }

    [Fact]
    public void CommitAsks_AReplyThatNeverPutTheQuestion_LeavesTheAskUncharged()
    {
        var clarifications = new Clarifications();

        clarifications.Ask("applies_to", Named("ct900", "ct900ent"), spendsReset: false);
        clarifications.CommitAsks("I'm sorry to hear that. What is going wrong?");
        clarifications.BeginTurn();

        var slot = clarifications.Read("applies_to");
        Assert.Equal(0, slot.NamedAsks);
        Assert.Equal(Clarifications.LastNamedKind.None, slot.LastNamed.Kind);
    }

    [Fact]
    public void CommitAsks_AReplyThatPutTheQuestion_ChargesTheAsk()
    {
        var clarifications = new Clarifications();
        var named = Named("ct900", "ct900ent");

        clarifications.Ask("applies_to", named, spendsReset: false);
        clarifications.CommitAsks("Is it the ct900 or the ct900ent?");
        clarifications.BeginTurn();

        var slot = clarifications.Read("applies_to");
        Assert.Equal(1, slot.NamedAsks);
        Assert.True(named.Names(slot.LastNamed));
    }

    private static Clarifications.LastNamed Named(params string[] values)
        => Clarifications.LastNamed.Of(new HashSet<string>(values, StringComparer.Ordinal));
}

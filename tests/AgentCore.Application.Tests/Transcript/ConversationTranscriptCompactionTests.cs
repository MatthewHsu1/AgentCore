using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// The summary row inside one transcript: the view it and the live rows build together, what a
/// compaction may cover, when one is refused, and which cuts must not reach under it.
/// </summary>
public sealed class ConversationTranscriptCompactionTests
{
    [Fact]
    public void Read_WithASummary_EmitsItFirstThenTheLiveRowsAboveWhatItCovers()
    {
        var transcript = FiveTurns();

        var row = transcript.Compact(Assistant("past"), coversUpTo: 3, transcript.Revision);

        Assert.NotNull(row);
        Assert.Equal(10, row.Ordinal);
        Assert.Equal(3, row.CoversUpTo);
        Assert.Equal(["past", "q2", "a2", "q3", "a3", "q4", "a4"], transcript.Read().Select(message => message.Text));
    }

    [Fact]
    public void Compact_ASecondSummary_ReplacesTheFirstAndCoversMore()
    {
        var transcript = FiveTurns();
        transcript.Compact(Assistant("past"), coversUpTo: 3, transcript.Revision);

        var row = transcript.Compact(Assistant("more past"), coversUpTo: 5, transcript.Revision);

        Assert.NotNull(row);
        Assert.Equal(11, row.Ordinal);
        Assert.Equal(["more past", "q3", "a3", "q4", "a4"], transcript.Read().Select(message => message.Text));
        Assert.Single(transcript.Messages, stored => stored.CoversUpTo is not null);
    }

    [Fact]
    public void Compact_ASummaryThatCoversLessThanTheStandingOne_Throws()
    {
        var transcript = FiveTurns();
        transcript.Compact(Assistant("past"), coversUpTo: 5, transcript.Revision);

        Assert.Throws<ArgumentOutOfRangeException>(() => transcript.Compact(Assistant("less"), coversUpTo: 3, transcript.Revision));
    }

    [Fact]
    public void Compact_RefusesACompactionReadBeforeTheWordsMoved()
    {
        var transcript = FiveTurns();
        var revision = transcript.Revision;
        transcript.BeginTurn(5);
        transcript.Append([User("q5")]);

        Assert.Null(transcript.Compact(Assistant("past"), coversUpTo: 3, revision));
        Assert.Null(transcript.Summary);
    }

    [Fact]
    public void Floor_ALiveRowReportsItsOwnOrdinal_AndTheSummaryReportsWhatItCovers()
    {
        var transcript = FiveTurns();
        transcript.Compact(Assistant("past"), coversUpTo: 3, transcript.Revision);

        var floor = transcript.Floor();

        Assert.NotNull(floor);
        Assert.Equal(3, floor.Value.CoversUpTo);
        Assert.Equal(
            [("past", 3), ("q2", 4), ("a2", 5), ("q3", 6), ("a3", 7)],
            floor.Value.Messages.Select(view => (view.Message.Text, view.LastOrdinal)));
    }

    [Fact]
    public void Floor_IsNothing_WhenOneTurnIsAllThatIsLive()
    {
        var transcript = new ConversationTranscript { ConversationId = "c1" };
        transcript.BeginTurn(0);
        transcript.Append([User("q0"), Assistant("a0")]);

        Assert.Null(transcript.Floor());

        // A summary that covers everything but the newest turn: nothing lies before that turn either.
        var five = FiveTurns();
        five.Compact(Assistant("past"), coversUpTo: 7, five.Revision);
        Assert.Null(five.Floor());
    }

    [Fact]
    public void Floor_LeavesTheSummaryOutOfTheNewestTurn_WhenTheTurnThatWroteItNeverLanded()
    {
        // The summary carries turn 5, but turn 5's own rows never landed. Turn 4 is still the newest
        // live turn, so it stays out of the floor.
        var transcript = FiveTurns();
        transcript.BeginTurn(5);
        transcript.Compact(Assistant("past"), coversUpTo: 3, transcript.Revision);

        var floor = transcript.Floor();

        Assert.NotNull(floor);
        Assert.Equal(["past", "q2", "a2", "q3", "a3"], floor.Value.Messages.Select(view => view.Message.Text));
    }

    [Fact]
    public void CanTruncateFrom_UnderASummary_OnlyARowAboveWhatItCoversWillDo()
    {
        var transcript = FiveTurns();
        Assert.True(transcript.CanTruncateFrom(null));
        Assert.True(transcript.CanTruncateFrom("nobody"));

        transcript.Compact(Assistant("past"), coversUpTo: 3, transcript.Revision);

        Assert.False(transcript.CanTruncateFrom(null));
        Assert.False(transcript.CanTruncateFrom("nobody"));
        Assert.True(transcript.CanTruncateFrom(transcript.Messages.Single(stored => stored.Ordinal == 4).MessageId));
    }

    [Fact]
    public void TruncateFrom_UnderWhatTheSummaryCovers_Refuses()
    {
        var transcript = FiveTurns();
        transcript.Compact(Assistant("past"), coversUpTo: 3, transcript.Revision);

        Assert.Throws<InvalidOperationException>(() => transcript.TruncateFrom(3));
    }

    [Fact]
    public void TruncateFrom_AboveWhatTheSummaryCovers_CutsTheLiveRowsAndKeepsTheSummary()
    {
        // The summary sits at ordinal 10, above the cut, and stays: it was written after the rows it
        // does not cover, so its ordinal says nothing about where the cut falls.
        var transcript = FiveTurns();
        transcript.Compact(Assistant("past"), coversUpTo: 3, transcript.Revision);

        var withdrawn = transcript.TruncateFrom(6);

        Assert.Equal(new WithdrawnTurns(3, 4), withdrawn);
        Assert.Equal(["past", "q2", "a2"], transcript.Read().Select(message => message.Text));
        Assert.NotNull(transcript.Summary);
        Assert.Equal(5, transcript.LastAssistantOrdinal);
    }

    [Fact]
    public void Resync_WithTheSummaryAndTheRowsAboveIt_BuildsTheSameView()
    {
        var transcript = new ConversationTranscript { ConversationId = "c1" };

        transcript.Resync(
            [Row(4, 2, "q2", "m4"), Row(5, 2, "a2", "m5"), Summary(6, 3, "past", coversUpTo: 3), Row(7, 3, "q3", "m7")],
            nextOrdinal: 8);

        Assert.Equal(["past", "q2", "a2", "q3"], transcript.Read().Select(message => message.Text));
        Assert.Equal(3, transcript.Summary?.CoversUpTo);
        Assert.Equal(5, transcript.LastAssistantOrdinal);
    }

    [Fact]
    public void TruncateLastReply_NeverRewritesTheSummary()
    {
        // The summary is an assistant message with text, in the same turn as the reply. A barge-in
        // must cut the reply and leave the summary whole.
        var transcript = FiveTurns();
        transcript.BeginTurn(5);
        transcript.Compact(Assistant("past"), coversUpTo: 3, transcript.Revision);
        transcript.Append([User("q5"), Assistant("a5 in full")]);

        var rows = transcript.TruncateLastReply("a5");

        Assert.Equal("a5", Assert.Single(rows).Content.Text);
        Assert.Equal("past", transcript.Summary?.Message.Text);
    }

    /// <summary>Turns 0-4, ordinals 0-9: q0/a0 .. q4/a4.</summary>
    private static ConversationTranscript FiveTurns()
    {
        var transcript = new ConversationTranscript { ConversationId = "c1" };
        for (var turn = 0; turn < 5; turn++)
        {
            transcript.BeginTurn(turn);
            transcript.Append([User($"q{turn}"), Assistant($"a{turn}")]);
        }

        return transcript;
    }

    /// <summary>A row whose role follows its text: a question is the user's, anything else the assistant's.</summary>
    private static ConversationMessage Row(int ordinal, int turnIndex, string text, string messageId)
        => new("c1", ordinal, turnIndex, new ChatMessage(text.StartsWith('q') ? ChatRole.User : ChatRole.Assistant, text), messageId);

    private static ConversationMessage Summary(int ordinal, int turnIndex, string text, int coversUpTo)
        => new("c1", ordinal, turnIndex, Assistant(text), $"s{ordinal}") { CoversUpTo = coversUpTo };

    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);
}

using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// Pins what happens when a caller sends an earlier message again: what is withdrawn, what is kept,
/// and which numbers refuse to be wound back with it.
/// </summary>
public sealed class CallTranscriptEditTests
{
    [Fact]
    public void Append_NamesEveryRow_AndKeepsTheNameTheCallerGaveTheFirst()
    {
        var transcript = new CallTranscript { CallId = "call-1" };

        var rows = transcript.Append([User("hello"), Assistant("hi")], firstMessageId: "caller-1");

        Assert.Equal("caller-1", rows[0].MessageId);

        // The reply is named too. It has to be: the message an edit hangs off is usually a reply, and
        // no caller can have named one in advance.
        Assert.NotNull(rows[1].MessageId);
        Assert.NotEqual(rows[0].MessageId, rows[1].MessageId);
    }

    [Fact]
    public void TruncateFrom_DropsTheTailAndLeavesTheMarksWhereTheyAre()
    {
        var transcript = new CallTranscript { CallId = "call-1" };
        transcript.BeginTurn(0);
        var first = transcript.Append([User("q1"), Assistant("a1")], "caller-1");
        transcript.BeginTurn(1);
        transcript.Append([User("q2"), Assistant("a2")], "caller-2");

        var went = transcript.TruncateFrom(transcript.OrdinalOf(first[1].MessageId!)!.Value + 1);

        Assert.Equal(new WithdrawnTurns(1, 1), went);
        Assert.Equal(["q1", "a1"], transcript.Read().Select(message => message.Text));

        // The withdrawn places stay spent. Store 3 is append-only and still holds rows against the
        // turns that stood in them, so reissuing either number would put two turns in one place.
        Assert.Equal(4, transcript.NextOrdinal);
        Assert.Equal(1, transcript.TurnIndex);
    }

    [Fact]
    public void TruncateFrom_AimsTheNextBargeInAtTheReplyThatSurvived()
    {
        var transcript = new CallTranscript { CallId = "call-1" };
        transcript.BeginTurn(0);
        var first = transcript.Append([User("q1"), Assistant("a1")], "caller-1");
        transcript.BeginTurn(1);
        transcript.Append([User("q2"), Assistant("a2")], "caller-2");

        transcript.TruncateFrom(transcript.OrdinalOf(first[1].MessageId!)!.Value + 1);

        // Without this a barge-in after an edit would aim at an ordinal no row holds any more, and
        // the cut would be a silent no-op.
        Assert.Equal(first[1].Ordinal, transcript.LastAssistantOrdinal);
    }

    [Fact]
    public void OrdinalOf_AMessageTheCallDoesNotHold_IsNull()
    {
        var transcript = new CallTranscript { CallId = "call-1" };
        transcript.Append([User("hello")], "caller-1");

        Assert.Null(transcript.OrdinalOf("never-stored"));
    }

    [Fact]
    public void Resume_PrefersTheStoredMarksOverTheRowsThatSurvived()
    {
        // A call that was edited: the rows of turns 1 and 2 are gone, so the rows alone would say the
        // next turn is 1 and the next ordinal 2 — both of which a deleted row already used.
        var transcript = new CallTranscript { CallId = "call-1" };

        var next = transcript.Resume(
            [
                new CallMessage("call-1", 0, 0, User("q1"), "m0"),
                new CallMessage("call-1", 1, 0, Assistant("a1"), "m1"),
            ],
            new TranscriptMarks(NextOrdinal: 6, NextTurnIndex: 3));

        Assert.Equal(3, next);
        Assert.Equal(6, transcript.NextOrdinal);
    }

    [Fact]
    public void Resume_SetsTheReplyABargeInWouldCut()
    {
        var transcript = new CallTranscript { CallId = "call-1" };

        transcript.Resume(
            [
                new CallMessage("call-1", 0, 0, User("q1"), "m0"),
                new CallMessage("call-1", 1, 0, Assistant("a1"), "m1"),

                // A tool-calling turn's textless assistant message is not a reply anybody can be cut
                // off in the middle of, so it must not be what the next barge-in aims at.
                new CallMessage("call-1", 2, 0, new ChatMessage(ChatRole.Assistant, []), "m2"),
            ],
            new TranscriptMarks(NextOrdinal: 3, NextTurnIndex: 1));

        Assert.Equal(1, transcript.LastAssistantOrdinal);
    }

    [Fact]
    public void Resume_ThenTruncateLastReply_ActuallyCuts()
    {
        // A resumed call has to know which reply a barge-in aims at. With no ordinal to aim at,
        // TruncateLastReply returns an empty list, which the provider reports as a write it declined
        // rather than as a cut it lost — so the caller is silently not heard.
        var transcript = new CallTranscript { CallId = "call-1" };
        transcript.Resume(
            [
                new CallMessage("call-1", 0, 0, User("q1"), "m0"),
                new CallMessage("call-1", 1, 0, Assistant("it ships on Friday"), "m1"),
            ],
            new TranscriptMarks(NextOrdinal: 2, NextTurnIndex: 1));

        var rows = transcript.TruncateLastReply("it ships");

        Assert.Single(rows);
        Assert.Equal("it ships", rows[0].Content.Text);
    }

    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);
}

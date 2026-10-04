using AgentCore.AspNetCore.Vendors.OpenAiLive.Call;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>An append carries at most 500 tokens: pieces are at most 1,500 characters, cut at a sentence end.</summary>
    public sealed class CommentaryPiecesTests
    {
        [Fact]
        public void AShortReplyIsOnePiece()
        {
            Assert.Equal(["The F80 tops out at 10.5 mph."], CommentaryPieces.Split("The F80 tops out at 10.5 mph."));
        }

        [Fact]
        public void ALongReplyIsCutAtSentenceEndsUnderTheLimit()
        {
            string text = string.Join(' ', Enumerable.Range(1, 200).Select(n => $"Sentence number {n:D3} is here."));

            IReadOnlyList<string> pieces = CommentaryPieces.Split(text);

            Assert.True(pieces.Count > 1);
            Assert.All(pieces, piece => Assert.True(piece.Length <= 1500 && piece.EndsWith('.')));
            Assert.Equal(text, string.Join(' ', pieces));
        }

        [Fact]
        public void TextWithNoSentenceEndOrSpaceIsCutAtTheLimit()
        {
            Assert.Equal([1500, 500], CommentaryPieces.Split(new string('x', 2000)).Select(piece => piece.Length));
        }
    }
}

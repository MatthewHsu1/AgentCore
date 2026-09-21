using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript
{
    public sealed class ReaderContentTests
    {
        [Fact]
        public void Strip_MessageWithoutReaderContent_ReturnsTheSameInstance()
        {
            ChatMessage plain = new(ChatRole.User, "hello");

            ChatMessage stripped = Assert.Single(ReaderContent.Strip([plain]));

            Assert.Same(plain, stripped);
        }

        [Fact]
        public void Strip_MessageWithReaderContent_CopiesItWithoutThatContentAndLeavesTheOriginalWhole()
        {
            ChatMessage published = new(ChatRole.Tool, [new FunctionResultContent("call-1", "published")])
            {
                MessageId = "m-7",
                AuthorName = "tool",
            };
            published.Contents.Add(new FileContent { Name = "report.md", FileId = "out/report.md", Kept = true });

            ChatMessage stripped = Assert.Single(ReaderContent.Strip([published]));

            Assert.NotSame(published, stripped);
            Assert.Equal("m-7", stripped.MessageId);
            Assert.Equal("tool", stripped.AuthorName);
            Assert.Equal(ChatRole.Tool, stripped.Role);
            _ = Assert.IsType<FunctionResultContent>(Assert.Single(stripped.Contents));
            Assert.Equal(2, published.Contents.Count);
        }
    }
}

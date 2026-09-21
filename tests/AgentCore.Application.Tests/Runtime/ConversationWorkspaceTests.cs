using AgentCore.Application.Runtime;
using Xunit;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// <see cref="ConversationWorkspace"/> in isolation: the folder it creates and the folder it deletes.
    /// </summary>
    public sealed class ConversationWorkspaceTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "agentcore-ws-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Fact]
        public void Create_MakesTheFolder()
        {
            ConversationWorkspace workspace = ConversationWorkspace.Create(_root, "conversation-1");

            Assert.Equal(Path.Combine(_root, "conversation-1"), workspace.Path);
            Assert.True(Directory.Exists(workspace.Path));
        }

        [Fact]
        public void Delete_RemovesTheFolder_AndIsSafeToCallTwice()
        {
            ConversationWorkspace workspace = ConversationWorkspace.Create(_root, "conversation-1");

            workspace.Delete(logger: null);
            Assert.False(Directory.Exists(workspace.Path));

            Exception? exception = Record.Exception(() => workspace.Delete(logger: null));
            Assert.Null(exception);
        }

        [Theory]
        [InlineData("../escape")]
        [InlineData("a/b")]
        [InlineData(@"a\b")]
        [InlineData("..")]
        [InlineData(".")]
        [InlineData("")]
        [InlineData("   ")]
        public void Create_RefusesAConversationIdThatCouldEscapeTheRoot(string conversationId)
        {
            _ = Assert.Throws<ArgumentException>(() => ConversationWorkspace.Create(_root, conversationId));
            Assert.False(Directory.Exists(_root));
        }

        /// <summary>
        /// <c>GetFullPath</c> normalises a trailing space or period out of the last segment on Windows,
        /// so a naive containment check would let " ." alias the root and "conversation-1." alias a sibling
        /// folder. On Windows this must throw. On Linux the segment is a legal folder name and is not an
        /// alias of anything, so it is fine for <c>Create</c> to make it — as long as the folder it makes
        /// is strictly under root and named exactly what the caller asked for.
        /// </summary>
        [Theory]
        [InlineData(" .")]
        [InlineData("conversation-1.")]
        public void Create_RefusesOrExactlyHonoursAConversationIdWithATrailingSpaceOrPeriod(string conversationId)
        {
            if (OperatingSystem.IsWindows())
            {
                _ = Assert.ThrowsAny<ArgumentException>(() => ConversationWorkspace.Create(_root, conversationId));
                Assert.False(Directory.Exists(_root));
                return;
            }

            ConversationWorkspace workspace = ConversationWorkspace.Create(_root, conversationId);

            Assert.StartsWith(_root + Path.DirectorySeparatorChar, workspace.Path, StringComparison.Ordinal);
            Assert.Equal(conversationId, Path.GetFileName(workspace.Path));
        }

        [Fact]
        public void Create_TwiceWithTheSameConversationId_ReusesTheFolderAndKeepsWhatWasWrittenIntoIt()
        {
            ConversationWorkspace first = ConversationWorkspace.Create(_root, "conversation-1");
            string marker = Path.Combine(first.Path, "marker.txt");
            File.WriteAllText(marker, "kept");

            ConversationWorkspace second = ConversationWorkspace.Create(_root, "conversation-1");

            Assert.Equal(first.Path, second.Path);
            Assert.Equal("kept", File.ReadAllText(marker));
        }
    }
}

using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Builtin;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Tests.Tools
{
    /// <summary>
    /// A background child runs with no turn, so <c>file.publish</c> finds the owning conversation on the stamp
    /// its session carries and stores the file under the parent conversation.
    /// </summary>
    public sealed class FilePublishBackgroundChildTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "agentcore-publish-child-" + Guid.NewGuid().ToString("N"));

        public FilePublishBackgroundChildTests()
        {
            _ = Directory.CreateDirectory(Path.Combine(_root, "conversation-9"));
        }

        public void Dispose()
        {
            Directory.Delete(_root, recursive: true);
        }

        [Fact]
        public async Task AChildThatPublishes_StoresTheFileUnderTheParentConversation()
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            File.WriteAllText(Path.Combine(_root, "conversation-9", "rows.csv"), "a,b");
            RecordingBlobStore blobs = new();

            AITool publish = new FilePublishToolDefinition().Build(
                new ToolConfiguration { Id = "publish", Kind = ToolKind.Builtin, Uses = BuiltinToolNames.FilePublish },
                new BuiltinToolPorts(ChatClients: null, Blobs: blobs, WorkspaceRoot: _root));

            ToolCallingChatClient childModel = new(
                "published",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = "rows.csv" });

            BackgroundChildAgent child = new(new ChatClientAgent(childModel, new ChatClientAgentOptions { ChatOptions = new ChatOptions { Tools = [publish] } }));
            SessionCreatingProvider creating = new(child);
            ChatClientAgent parent = new(new ScriptedChatClient("parent"), new ChatClientAgentOptions { AIContextProviders = [creating] });
            TurnInvocation turn = new() { ConversationId = "conversation-9", TurnIndex = 0, Stage = "s", Workspace = Path.Combine(_root, "conversation-9") };

            _ = await parent.RunAsync("go", await parent.CreateSessionAsync(token), turn.RunOptions(), token);

            // The child runs on its stamped session with no run options, as the provider runs it.
            _ = await child.RunAsync("publish the rows", creating.Created!, options: null, token);

            Assert.Equal(["publish"], childModel.Called);
            string result = Assert.Single(childModel.ToolResults);
            Assert.Contains("sandbox:/rows.csv", result, StringComparison.Ordinal);
            Assert.DoesNotContain(ToolErrorResult.ErrorProperty + "\":true", result, StringComparison.Ordinal);
            _ = Assert.Single(blobs.Blobs.Keys, key => key == ("conversation-9", "rows.csv"));
        }

        private sealed class SessionCreatingProvider(AIAgent child) : AIContextProvider
        {
            public AgentSession? Created { get; private set; }

            protected override async ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken = default)
            {
                Created = await child.CreateSessionAsync(cancellationToken);
                return new AIContext();
            }
        }
    }
}

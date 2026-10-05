using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.AspNetCore.Tests.DependencyInjection.StartedHostFixture;

namespace AgentCore.AspNetCore.Tests.DependencyInjection
{
    /// <summary>
    /// <see cref="AgentCoreOptions.UseWorkspace(string)"/> and the folder it binds a conversation's
    /// <see cref="ConversationSession.Workspace"/> to.
    /// </summary>
    public sealed class WorkspaceStartupTests : IDisposable
    {
        private readonly string _tempRoot =
            Path.Combine(Path.GetTempPath(), "agentcore-ws-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void UseWorkspace_AnEmptyPath_ThrowsArgumentException()
        {
            AgentCoreOptions options = new();

            _ = Assert.Throws<ArgumentException>(() => options.UseWorkspace(""));
        }

        [Fact]
        public async Task AHostBoundToAWorkspaceRoot_CreatesTheConversationsFolderWhenAConversationOpens()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml, options => options.UseWorkspace(_tempRoot));

            IConversationSessions sessions = provider.GetRequiredService<EntryRegistry>().Sessions;
            ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, TestContext.Current.CancellationToken);

            Assert.Equal(Path.Combine(_tempRoot, "conversation-1"), session.Workspace);
            Assert.True(Directory.Exists(Path.Combine(_tempRoot, "conversation-1")));
        }

        [Fact]
        public async Task AHostWithNoWorkspaceBound_CreatesNothingAndLeavesWorkspaceNull()
        {
            using StartedHost provider = await BuildAsync(OneAgentYaml);

            IConversationSessions sessions = provider.GetRequiredService<EntryRegistry>().Sessions;
            ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, TestContext.Current.CancellationToken);

            Assert.Null(session.Workspace);
        }

        [Fact]
        public async Task Boot_SweepsTheWorkspaceRootExactlyOnce()
        {
            FakeTimeProvider clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            _ = Directory.CreateDirectory(_tempRoot);

            string stale = MarkedFolder("stale", clock, TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
            string fresh = MarkedFolder("fresh", clock, TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));

            using StartedHost provider = await BuildAsync(OneAgentYaml, options =>
            {
                _ = options.UseWorkspace(_tempRoot);
                options.TimeProvider = clock;
            });

            Assert.False(Directory.Exists(stale));
            Assert.True(Directory.Exists(fresh));

            // A folder that only becomes old enough to sweep after boot is never touched: nothing runs the
            // sweep a second time, whether on a timer or when a later conversation opens.
            string lateStale = MarkedFolder("late-stale", clock, TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

            IConversationSessions sessions = provider.GetRequiredService<EntryRegistry>().Sessions;
            _ = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, TestContext.Current.CancellationToken);

            Assert.True(Directory.Exists(lateStale));
            Assert.True(Directory.Exists(Path.Combine(_tempRoot, "conversation-1")));
        }

        private string MarkedFolder(string name, FakeTimeProvider clock, TimeSpan age)
        {
            string folder = Directory.CreateDirectory(Path.Combine(_tempRoot, name)).FullName;
            File.WriteAllText(ConversationWorkspace.MarkerPathFor(folder), string.Empty);
            Directory.SetLastWriteTimeUtc(folder, clock.GetUtcNow().UtcDateTime - age);
            return folder;
        }

        [Fact]
        public async Task ARootThatCannotBeCreated_FailsStartupWithThePathAndTheOption()
        {
            string blockingFile = Path.Combine(_tempRoot, "blocks-the-root");
            _ = Directory.CreateDirectory(_tempRoot);
            await File.WriteAllTextAsync(blockingFile, "not a directory", TestContext.Current.CancellationToken);
            string unusableRoot = Path.Combine(blockingFile, "x");

            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                () => BuildAsync(OneAgentYaml, options => options.UseWorkspace(unusableRoot)));

            Assert.Contains("options.UseWorkspace(", failure.Message, StringComparison.Ordinal);
            Assert.Contains(unusableRoot, failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AMemoryAgent_WithAWorkspaceRootBound_Starts()
        {
            using StartedHost provider = await BuildAsync(MemoryAgentYaml, options => options.UseWorkspace(_tempRoot));

            IConversationSessions sessions = provider.GetRequiredService<EntryRegistry>().Sessions;
            ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, TestContext.Current.CancellationToken);

            Assert.StartsWith(_tempRoot, session.Workspace, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AMemoryAgent_WithNoWorkspaceRootBound_FailsStartupNamingUseWorkspace()
        {
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                () => BuildAsync(MemoryAgentYaml));

            Assert.Contains("options.UseWorkspace(", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AMemoryAgent_WithARootThatCannotBeCreated_FailsStartupWithThePathAndTheOption()
        {
            string blockingFile = Path.Combine(_tempRoot, "blocks-the-root");
            _ = Directory.CreateDirectory(_tempRoot);
            await File.WriteAllTextAsync(blockingFile, "not a directory", TestContext.Current.CancellationToken);
            string unusableRoot = Path.Combine(blockingFile, "x");

            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                () => BuildAsync(MemoryAgentYaml, options => options.UseWorkspace(unusableRoot)));

            Assert.Contains("options.UseWorkspace(", failure.Message, StringComparison.Ordinal);
            Assert.Contains(unusableRoot, failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AFilesAgent_WithAWorkspaceRootBound_Starts()
        {
            using StartedHost provider = await BuildAsync(FilesAgentYaml, options => options.UseWorkspace(_tempRoot));

            IConversationSessions sessions = provider.GetRequiredService<EntryRegistry>().Sessions;
            ConversationSession session = await sessions.GetOrOpenAsync(SingleEntrySessionFactories.MainEntry, "conversation-1", null, TestContext.Current.CancellationToken);

            Assert.StartsWith(_tempRoot, session.Workspace, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AFilesAgent_WithNoWorkspaceRootBound_FailsStartupNamingUseWorkspace()
        {
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                () => BuildAsync(FilesAgentYaml));

            Assert.Contains("options.UseWorkspace(", failure.Message, StringComparison.Ordinal);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }

        private const string MemoryAgentYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything", memory: { store: workspace } }
        providers:
          conversation:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        entries:
          main:
            agent: only
        """;

        private const string FilesAgentYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything", files: { store: workspace } }
        providers:
          conversation:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        entries:
          main:
            agent: only
        """;
    }
}

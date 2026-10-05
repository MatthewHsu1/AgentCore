using System.Diagnostics;
using System.Reflection;
using Microsoft.Agents.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// The workspace marker sits outside the folder, as <c>root/conversationId.agentcore-workspace</c>, so a
    /// model working inside the folder — listing it, indexing it for memory, or cloning a repository into it —
    /// never sees anything AgentCore itself put there.
    /// </summary>
#pragma warning disable MAAI001 // File-store and memory-provider types are evaluation-only in Microsoft.Agents.AI 1.21.0.
    public sealed class ConversationWorkspaceMarkerVisibilityTests : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("agentcore-ws-marker-vis-").FullName;

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Fact]
        public async Task TheMarkerDoesNotShowUpInTheFileAccessListing()
        {
            ConversationWorkspace workspace = ConversationWorkspace.Create(_root, "c1");
            FileSystemAgentFileStore store = new(workspace.Path);

            IReadOnlyList<FileStoreEntry> entries = await store.ListChildrenAsync("", TestContext.Current.CancellationToken);

            Assert.DoesNotContain(entries, entry => entry.Name == ConversationWorkspace.MarkerFileName);
        }

        [Fact]
        public async Task TheMarkerDoesNotShowUpInTheMemoryIndex()
        {
            _ = ConversationWorkspace.Create(_root, "c1");
            FileSystemAgentFileStore store = new(_root);
            await File.WriteAllTextAsync(Path.Combine(_root, "c1", "note.md"), "x", TestContext.Current.CancellationToken);
            FileMemoryProvider memory = new(store, _ => new FileMemoryState { WorkingFolder = "c1" });

            MethodInfo rebuild = typeof(FileMemoryProvider)
                .GetMethod("RebuildMemoryIndexAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)rebuild.Invoke(memory, [new FileMemoryState { WorkingFolder = "c1" }, TestContext.Current.CancellationToken])!;

            string index = await File.ReadAllTextAsync(Path.Combine(_root, "c1", "memories.md"), TestContext.Current.CancellationToken);
            Assert.DoesNotContain(ConversationWorkspace.MarkerFileName, index, StringComparison.Ordinal);
        }

        [Fact]
        public void GitCloneIntoTheWorkspaceSucceeds()
        {
            string src = Directory.CreateDirectory(Path.Combine(_root, "src-repo")).FullName;
            Run(src, "git", "init", "-q");
            File.WriteAllText(Path.Combine(src, "a.txt"), "a");
            Run(src, "git", "-c", "user.email=a@b", "-c", "user.name=a", "add", ".");
            Run(src, "git", "-c", "user.email=a@b", "-c", "user.name=a", "commit", "-qm", "x");

            ConversationWorkspace workspace = ConversationWorkspace.Create(_root, "c1");

            Assert.Equal(0, Run(workspace.Path, "git", "clone", "-q", src, "."));
        }

        private static int Run(string cwd, string file, params string[] args)
        {
            ProcessStartInfo psi = new(file) { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using Process process = Process.Start(psi)!;
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            TestContext.Current.SendDiagnosticMessage($"{file} {string.Join(' ', args)} -> {process.ExitCode} {error}");
            return process.ExitCode;
        }
    }
#pragma warning restore MAAI001
}

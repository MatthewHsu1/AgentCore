using System.Net;
using System.Text;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// The HTTP Responses path runs every turn of a live conversation on the one session the owner holds, so
    /// consecutive turns share one shell and read the transcript at most once per load (issue #24).
    /// An idle unload ends that session: the next turn gets a new shell and an empty workspace folder, and
    /// reads the history back from the store.
    /// </summary>
    public sealed class HttpSessionOwnerReuseTests : IDisposable
    {
        private const string Yaml =
            """
            apiVersion: agentcore/v1
            agents:
              defaults:
                model: { ref: reply }
              items:
                - { id: greeter, instructions: "run the shell tool once, then answer done", shell: { kind: local } }
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: reply }
              conversations: { kind: test }
            entries:
              main:
                agent: greeter
            """;

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "agentcore-owner-reuse-" + Guid.NewGuid().ToString("N"));

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Fact(Timeout = 60_000)]
        public async Task TwoResponsesTurns_OnANewConversation_LeaveOneLiveShellAndMakeNoFullTranscriptReads()
        {
            CountingConversationStore store = new();
            ShellPidChatClient client = new();
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                Yaml, client, options => options.UseWorkspace(_root).UseConversationStores(store));
            string conversation = "conv_" + Guid.NewGuid().ToString("N");

            using HttpResponseMessage first = await PostAsync(host.Client, conversation, "go");
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            using HttpResponseMessage second = await PostAsync(host.Client, conversation, "go again");
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            Assert.Equal(2, client.ToolResults.Count);
            int firstPid = ShellPidChatClient.ParsePid(client.ToolResults[0]);
            int secondPid = ShellPidChatClient.ParsePid(client.ToolResults[1]);

            // The second turn ran on the same live session, so the same shell served both.
            Assert.Equal(firstPid, secondPid);
            Assert.True(Directory.Exists($"/proc/{firstPid}"), $"the one shell (pid {firstPid}) should still be alive.");

            // Turn 1 opens a brand-new conversation and reads nothing (no words exist yet); turn 2 reuses
            // the live session instead of rebuilding it, so it reads nothing either.
            Assert.Equal(0, store.FullReads);
        }

        [Fact(Timeout = 60_000)]
        public async Task AConversationThatAlreadyHasWords_OnlyTheFirstTurnReadsTheTranscript_LaterLiveTurnsReadNone()
        {
            CountingConversationStore store = new();
            ShellPidChatClient client = new();
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                Yaml, client, options => options.UseWorkspace(_root).UseConversationStores(store));
            string conversation = "conv_" + Guid.NewGuid().ToString("N");

            // Seeded directly on the store, as another process (or an earlier run of this one) would
            // have left it: this process has never built a session for this id yet.
            _ = await store.CreateAsync(conversation, Ct);
            _ = await store.AppendAsync(
                conversation,
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "earlier turn"), "m0")],
                state: null,
                Ct);

            using HttpResponseMessage first = await PostAsync(host.Client, conversation, "go");
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(1, store.FullReads);

            using HttpResponseMessage second = await PostAsync(host.Client, conversation, "go again");
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal(1, store.FullReads);

            using HttpResponseMessage third = await PostAsync(host.Client, conversation, "and once more");
            Assert.Equal(HttpStatusCode.OK, third.StatusCode);
            Assert.Equal(1, store.FullReads);
        }

        [Fact(Timeout = 60_000)]
        public async Task AThirdTurnElevenMinutesLater_GetsANewShellWithHistoryIntactAndAnEmptyWorkspaceFolder()
        {
            FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
            ShellPidChatClient client = new();
            CountingConversationStore store = new();
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                Yaml,
                client,
                options =>
                {
                    _ = options.UseWorkspace(_root).UseConversationStores(store);
                    options.TimeProvider = clock;
                });
            string conversation = "conv_" + Guid.NewGuid().ToString("N");

            client.Command = "echo $$; touch left-behind.txt";
            using HttpResponseMessage first = await PostAsync(host.Client, conversation, "go");
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            string folder = (await ResolveLiveAsync(host, conversation)).Workspace!;
            Assert.True(File.Exists(Path.Combine(folder, "left-behind.txt")), "the first turn's shell should write into the workspace.");

            client.Command = "echo $$";
            using HttpResponseMessage second = await PostAsync(host.Client, conversation, "go again");
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            // Past the 10-minute default idle timeout: the live session unloads (no words lost, the store
            // still holds them), its shell stops, and its workspace folder is deleted.
            clock.Advance(TimeSpan.FromMinutes(11));

            using HttpResponseMessage third = await PostAsync(host.Client, conversation, "one more time");
            Assert.Equal(HttpStatusCode.OK, third.StatusCode);

            Assert.Equal(3, client.ToolResults.Count);
            int secondPid = ShellPidChatClient.ParsePid(client.ToolResults[1]);
            int thirdPid = ShellPidChatClient.ParsePid(client.ToolResults[2]);

            Assert.NotEqual(secondPid, thirdPid);
            Assert.False(Directory.Exists($"/proc/{secondPid}"), "the unloaded turn's shell should have stopped.");
            Assert.True(Directory.Exists($"/proc/{thirdPid}"), "the third turn's own shell should be alive.");

            ConversationSession live = await ResolveLiveAsync(host, conversation);
            Assert.NotNull(live.Workspace);
            Assert.False(File.Exists(Path.Combine(folder, "left-behind.txt")), "the unload should delete the first session's folder.");
            Assert.Empty(Directory.EnumerateFileSystemEntries(live.Workspace!));

            // The third turn's first model call is the fifth request: each turn calls the model, then answers the tool.
            Assert.Equal(["go", "go again", "one more time"], client.UserWords[4]);
        }

        private static async Task<ConversationSession> ResolveLiveAsync(ResponsesHost host, string conversation)
        {
            IConversationSessions sessions = host.Services.GetRequiredService<EntryRegistry>().Sessions;
            return await sessions.TryGetAsync("main", conversation, Ct)
                ?? throw new InvalidOperationException("the conversation should still be live after the third turn.");
        }

        private static Task<HttpResponseMessage> PostAsync(HttpClient client, string conversation, string input)
        {
            HttpRequestMessage request = new(HttpMethod.Post, "/v1/main/responses")
            {
                Content = new StringContent(
                    /*lang=json,strict*/ $$"""{ "stream": false, "conversation": "{{conversation}}", "input": "{{input}}" }""",
                    Encoding.UTF8,
                    "application/json"),
            };

            return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        }
    }
}

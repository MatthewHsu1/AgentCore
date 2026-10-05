using System.Text.Json;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Agents.Graph;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// The non-streaming half of a graph participant's run. The MAF 1.21 workflow always streams its
    /// participants, so only a graph of our own reaches this path.
    /// </summary>
    public sealed class GraphParticipantAgentTests
    {
        [Fact]
        public async Task ARunWithoutStreamingCarriesTheGraphsTurnNestedOnTheOptionsAndTheSession()
        {
            TurnInvocation turn = new() { ConversationId = "conversation-7", TurnIndex = 3, Stage = "" };
            TurnProbeAgent inner = new();
            GraphParticipantAgent participant = new(inner);
            OuterRunAgent graph = new(async () =>
            {
                AgentSession session = await participant.CreateSessionAsync(TestContext.Current.CancellationToken);
                _ = await participant.RunAsync([new ChatMessage(ChatRole.User, "hi")], session, cancellationToken: TestContext.Current.CancellationToken);
            });
            TurnProbeAgent.Session graphSession = new();
            TurnRegistry.Set(graphSession, turn);

            _ = await graph.RunAsync([new ChatMessage(ChatRole.User, "hi")], graphSession, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(("conversation-7", 3, true), Facts(inner.FromOptions));
            Assert.Equal(("conversation-7", 3, true), Facts(inner.FromSession));
        }

        // A participant that published a file and then failed still hands the caller the file.
        [Fact]
        public async Task AFilePublishedBeforeTheRunThrowsStillReachesTheCaller()
        {
            TurnNotices notices = new();
            TurnInvocation turn = new() { ConversationId = "conversation-7", TurnIndex = 3, Stage = "", Notices = notices };
            GraphParticipantAgent participant = new(new PublishThenThrowAgent());
            OuterRunAgent graph = new(async () =>
            {
                AgentSession session = await participant.CreateSessionAsync(TestContext.Current.CancellationToken);
                _ = await participant.RunAsync([new ChatMessage(ChatRole.User, "hi")], session, cancellationToken: TestContext.Current.CancellationToken);
            });
            TurnProbeAgent.Session graphSession = new();
            TurnRegistry.Set(graphSession, turn);

            _ = await Assert.ThrowsAsync<InvalidOperationException>(
                () => graph.RunAsync([new ChatMessage(ChatRole.User, "hi")], graphSession, cancellationToken: TestContext.Current.CancellationToken));

            Assert.True(notices.Reader.TryRead(out NoticeContent? posted));
            FileNotice card = Assert.IsType<FileNotice>(posted);
            Assert.Equal(("rows.csv", "publisher"), (card.File.Name, card.Author));
        }

        private static (string?, int?, bool?) Facts(TurnInvocation? turn) => (turn?.ConversationId, turn?.TurnIndex, turn?.Nested);

        /// <summary>Publishes a file the way <c>file.publish</c> files it, under its tool call, then fails the run.</summary>
        private sealed class PublishThenThrowAgent : AIAgent
        {
            public override string? Name => "publisher";

            protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            {
                return new(new TurnProbeAgent.Session());
            }

            protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
                AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
                JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            protected override Task<AgentResponse> RunCoreAsync(
                IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
            {
                TurnRegistry.For(session)?.Files?.Publish(new FileContent { Name = "rows.csv", FileId = "rows.csv" }, "call-1");
                throw new InvalidOperationException("the participant failed after it published");
            }

            protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
                IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }
    }
}
